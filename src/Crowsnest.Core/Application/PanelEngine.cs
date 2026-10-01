using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Formatting;
using Crowsnest.Core.Domain.Grids;
using Crowsnest.Core.Domain.Tuning;

namespace Crowsnest.Core.Application;

/// <summary>Something to send to the sim, in order.</summary>
public abstract record SimCommand
{
    private SimCommand()
    {
    }

    public sealed record Write(ParameterId Id, int Value) : SimCommand;

    public sealed record Invoke(string EventName) : SimCommand;
}

/// <summary>What one event requires of the outside world. Sim commands go before the render.</summary>
public sealed record PanelEffects(IReadOnlyList<SimCommand> Sim, DisplayFrame? Frame)
{
    public static PanelEffects None { get; } = new([], null);
}

/// <summary>
/// The panel's behaviour with the I/O taken out: one <see cref="TuningSession"/> per
/// parameter, the page the panel is on, and the frame it shows (spec §5.7). Each call takes
/// one event and returns what to do about it; <see cref="PanelCoordinator"/> does it.
///
/// Pure and single-threaded, like the sessions it holds: time comes in as <c>now</c>, and the
/// coordinator's one event loop is the only caller.
/// </summary>
public sealed class PanelEngine : IPanelContext
{
    private static readonly FieldRole[] Roles = [FieldRole.Primary, FieldRole.Secondary, FieldRole.Tertiary];

    private readonly IReadOnlyDictionary<ParameterId, TuningSession> _sessions;
    private readonly PageNavigator _pages;
    private readonly IInputActionMap _actions;
    private readonly IReadOnlyList<IPanelBehaviour> _behaviours;

    // Set while behaviours run, so a grid change they make can ask for a redraw.
    private bool _behaviourRedraw;

    private SimConnectionState _sim = SimConnectionState.Disconnected;
    private long _revision;
    private long _ackSequence;
    private DateTimeOffset? _lastTurnAt;

    public PanelEngine(
        ParameterRegistry registry,
        IReadOnlyList<PanelPage> pages,
        IInputActionMap actions,
        TuningOptions options,
        IReadOnlyList<IPanelBehaviour>? behaviours = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(options);

        _pages = new PageNavigator(pages);
        foreach (PanelPage page in pages)
        {
            if (page.Fields.Count is 0 || page.Fields.Count > Roles.Length)
            {
                throw new ArgumentException($"Page '{page.Id}' has {page.Fields.Count} fields; a page shows 1 to {Roles.Length}.", nameof(pages));
            }

            foreach (ParameterId id in page.Fields.Where(id => !registry.TryGet(id, out _)))
            {
                throw new ArgumentException($"Page '{page.Id}' shows '{id}', which is not registered.", nameof(pages));
            }
        }

        _actions = actions;
        _behaviours = behaviours ?? [];

        // Every registered parameter gets a session, on a page or not, so switching pages is
        // instant and values are current when a page appears (spec §5.7).
        _sessions = registry.All.ToDictionary(p => p.Id, p => new TuningSession(p, options));
    }

    public PanelPage CurrentPage => _pages.Current;

    public TuningSession Session(ParameterId id) => _sessions[id];

    /// <summary>The frame for the current state, whether or not anything changed. For start-up and a device (re)joining.</summary>
    public DisplayFrame Render()
    {
        PanelPage page = _pages.Current;
        List<FieldDescriptor> fields = [];

        for (int i = 0; i < page.Fields.Count; i++)
        {
            TuningSession session = _sessions[page.Fields[i]];
            ParameterDefinition parameter = session.Parameter;
            string text = session.HasValue ? parameter.Formatter.Format(session.Displayed) : parameter.Formatter.Placeholder;

            // Only the tuned field shows a cursor, and only over a value it fits.
            Range? cursor = i == 0 && session.HasValue && CursorSpans.TryResolve(session.Cursor.DisplaySpan, text.Length, out Range span)
                ? span
                : null;

            fields.Add(new FieldDescriptor(
                Roles[i],
                parameter.Label,
                text,
                cursor,
                Pending: IsPending(session)));
        }

        return new DisplayFrame(
            ++_revision,
            _sim,
            new PageDescriptor(page.Id, page.Title, page.Layout, _pages.Index, _pages.Count),
            fields,
            Notice: null,
            AckSequence: _ackSequence);
    }

    /// <summary>
    /// A device has (re)joined: the first connection, or a panel that restarted under an open
    /// port. A restarted panel numbers its inputs from 1 again and has a blank screen, so the ack
    /// and the acceleration clock start over and it gets the current frame.
    /// </summary>
    public PanelEffects OnDeviceJoined()
    {
        _ackSequence = 0;
        _lastTurnAt = null;
        return new PanelEffects([], Render());
    }

    public PanelEffects OnSimConnection(SimConnectionState state)
    {
        if (state == _sim)
        {
            return PanelEffects.None;
        }

        _sim = state;
        return new PanelEffects([], Render());
    }

    public PanelEffects OnSnapshot(ParameterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Behaviours see everything first, watches included: a spacing change must rebuild the
        // grid before a value on the new grid is judged against the old one.
        _behaviourRedraw = false;
        foreach (IPanelBehaviour behaviour in _behaviours)
        {
            behaviour.OnSnapshot(snapshot, this);
        }

        bool visible = _behaviourRedraw;

        if (snapshot.Available && _sessions.TryGetValue(snapshot.Id, out TuningSession? session))
        {
            bool wasPending = IsPending(session);
            TuningOutcome outcome = session.ObserveSimValue(snapshot.CanonicalValue);
            visible |= (outcome.DisplayChanged || IsPending(session) != wasPending) && IsOnScreen(snapshot.Id);
        }

        return visible ? new PanelEffects([], Render()) : PanelEffects.None;
    }

    public PanelEffects OnInput(DeviceInputEvent input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Acknowledge every input, even ignored ones, so the device can stop showing it as in flight.
        bool acked = input.Sequence > _ackSequence;
        _ackSequence = Math.Max(_ackSequence, input.Sequence);

        BridgeCommand? command = _actions.Resolve(input, new PanelState(_pages.Current));
        PanelEffects effects = command is null ? PanelEffects.None : Execute(command, input, now);

        return effects.Frame is null && acked ? effects with { Frame = Render() } : effects;
    }

    /// <summary>Runs every session's timers: the write debounce, the forced write, the settle timeout.</summary>
    public PanelEffects OnTick(DateTimeOffset now)
    {
        List<SimCommand> sim = [];
        bool redraw = false;

        foreach (TuningSession session in _sessions.Values)
        {
            bool wasPending = IsPending(session);
            TuningOutcome outcome = session.Tick(now);
            if (outcome.WriteRequest is { } value)
            {
                sim.Add(new SimCommand.Write(session.Parameter.Id, value));
            }

            // A write changes nothing on screen; a rejection does, even back to the same value.
            bool visible = outcome.DisplayChanged || IsPending(session) != wasPending;
            redraw |= visible && IsOnScreen(session.Parameter.Id);
        }

        return sim.Count == 0 && !redraw ? PanelEffects.None : new PanelEffects(sim, redraw ? Render() : null);
    }

    private PanelEffects Execute(BridgeCommand command, DeviceInputEvent input, DateTimeOffset now)
    {
        TuningSession tuned = _sessions[_pages.Current.Fields[0]];

        switch (command)
        {
            case BridgeCommand.AdjustValue adjust:
            {
                TimeSpan sinceLastTurn = _lastTurnAt is { } last ? input.At - last : TimeSpan.MaxValue;
                _lastTurnAt = input.At;

                TuningOutcome outcome = tuned.ApplyDetents(adjust.Detents, sinceLastTurn, now);
                return new PanelEffects(
                    outcome.WriteRequest is { } value ? [new SimCommand.Write(tuned.Parameter.Id, value)] : [],
                    outcome.DisplayChanged ? Render() : null);
            }

            case BridgeCommand.CycleCursor:
                return tuned.ToggleCursor().DisplayChanged ? new PanelEffects([], Render()) : PanelEffects.None;

            case BridgeCommand.SwapSlots when _pages.Current.SwapEvent is { } swap:
            {
                // Send what the pilot has dialled before swapping it, or the sim swaps the old value.
                List<SimCommand> sim = [];
                if (tuned.Flush(now).WriteRequest is { } value)
                {
                    sim.Add(new SimCommand.Write(tuned.Parameter.Id, value));
                }

                tuned.AcceptNextSimValue();
                sim.Add(new SimCommand.Invoke(swap));
                return new PanelEffects(sim, null);
            }

            case BridgeCommand.NextPage:
                _pages.Next();
                return new PanelEffects([], Render());

            case BridgeCommand.PreviousPage:
                _pages.Previous();
                return new PanelEffects([], Render());

            case BridgeCommand.GoToPage go when _pages.TryGoTo(go.PageId):
                return new PanelEffects([], Render());

            default:
                return PanelEffects.None;
        }
    }

    ParameterDefinition IPanelContext.Parameter(ParameterId id) => _sessions[id].Parameter;

    void IPanelContext.ReplaceGrid(ParameterId id, IValueGrid grid)
    {
        TuningSession session = _sessions[id];
        bool wasPending = IsPending(session);
        TuningOutcome outcome = session.ReplaceGrid(grid);
        _behaviourRedraw |= (outcome.DisplayChanged || IsPending(session) != wasPending) && IsOnScreen(id);
    }

    private bool IsOnScreen(ParameterId id) => _pages.Current.Fields.Contains(id);

    private static bool IsPending(TuningSession session) => session.Status == PendingWriteStatus.AwaitingConfirmation;
}

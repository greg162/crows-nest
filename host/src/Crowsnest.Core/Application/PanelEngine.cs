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

/// <summary>A frame for one panel.</summary>
public sealed record PanelFrame(string PanelId, DisplayFrame Frame);

/// <summary>What one event requires of the outside world. Sim commands go before the renders.</summary>
public sealed record PanelEffects(IReadOnlyList<SimCommand> Sim, IReadOnlyList<PanelFrame> Frames)
{
    public static PanelEffects None { get; } = new([], []);

    /// <summary>The frame for <paramref name="panelId"/>, or null if it needs none.</summary>
    public DisplayFrame? FrameFor(string panelId) => Frames.FirstOrDefault(f => f.PanelId == panelId)?.Frame;
}

/// <summary>
/// The panels' behaviour with the I/O taken out (spec §5.7, §6.2). One <see cref="TuningSession"/>
/// per parameter, shared by every panel, so two panels showing COM 1 tune the same value rather
/// than fighting over it. Each panel keeps its own page, revision, ack and turn timing, so panels
/// navigate independently. Each call takes one event and returns what to do about it, including
/// a frame for every panel whose screen it changed; <see cref="PanelCoordinator"/> does it.
///
/// Pure and single-threaded, like the sessions it holds: time comes in as <c>now</c>, and the
/// coordinator's one event loop is the only caller.
/// </summary>
public sealed class PanelEngine : IPanelContext
{
    private static readonly FieldRole[] Roles = [FieldRole.Primary, FieldRole.Secondary, FieldRole.Tertiary];

    private readonly IReadOnlyDictionary<ParameterId, TuningSession> _sessions;
    private readonly IReadOnlyList<PanelPage> _pages;
    private readonly IInputActionMap _actions;
    private readonly IReadOnlyList<IPanelBehaviour> _behaviours;
    private readonly Dictionary<string, PanelView> _panels = new(StringComparer.Ordinal);

    // Parameters whose look changed while behaviours ran, so the panels showing them redraw.
    private readonly HashSet<ParameterId> _behaviourChanged = [];

    private SimConnectionState _sim = SimConnectionState.Disconnected;

    /// <param name="pages">The pages every panel shows, until panels have assignments (spec §6.2).</param>
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

        _ = new PageNavigator(pages); // checks the list itself: not empty, no shared ids
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

        _pages = pages;
        _actions = actions;
        _behaviours = behaviours ?? [];

        // Every registered parameter gets a session, on a page or not, so switching pages is
        // instant and values are current when a page appears (spec §5.7).
        _sessions = registry.All.ToDictionary(p => p.Id, p => new TuningSession(p, options));
    }

    public IReadOnlyCollection<string> Panels => _panels.Keys;

    public PanelPage CurrentPage(string panelId) => View(panelId).Pages.Current;

    public TuningSession Session(ParameterId id) => _sessions[id];

    /// <summary>A panel has connected. It starts on the first page and gets its first frame.</summary>
    public PanelEffects Join(string panelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(panelId);

        PanelView view = new(panelId, new PageNavigator(_pages));
        if (!_panels.TryAdd(panelId, view))
        {
            throw new InvalidOperationException($"Panel '{panelId}' has already joined.");
        }

        return Redraw(view);
    }

    /// <summary>A panel has gone. Unknown ids are ignored, so leaving twice is harmless.</summary>
    public void Leave(string panelId) => _panels.Remove(panelId);

    /// <summary>
    /// A panel restarted under an open port. It numbers its inputs from 1 again and has a blank
    /// screen, so the ack and the acceleration clock start over and it gets the current frame.
    /// It keeps its page: the pilot was on it a moment ago.
    /// </summary>
    public PanelEffects OnPanelRestarted(string panelId)
    {
        PanelView view = View(panelId);
        view.AckSequence = 0;
        view.LastTurnAt = null;
        return Redraw(view);
    }

    /// <summary>The frame for a panel's current state, whether or not anything changed.</summary>
    public DisplayFrame Render(string panelId) => Render(View(panelId));

    public PanelEffects OnSimConnection(SimConnectionState state)
    {
        if (state == _sim)
        {
            return PanelEffects.None;
        }

        _sim = state;
        return new PanelEffects([], [.. _panels.Values.Select(v => new PanelFrame(v.Id, Render(v)))]);
    }

    public PanelEffects OnSnapshot(ParameterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Behaviours see everything first, watches included: a spacing change must rebuild the
        // grid before a value on the new grid is judged against the old one.
        _behaviourChanged.Clear();
        foreach (IPanelBehaviour behaviour in _behaviours)
        {
            behaviour.OnSnapshot(snapshot, this);
        }

        HashSet<ParameterId> changed = [.. _behaviourChanged];

        if (snapshot.Available && _sessions.TryGetValue(snapshot.Id, out TuningSession? session))
        {
            bool wasPending = IsPending(session);
            TuningOutcome outcome = session.ObserveSimValue(snapshot.CanonicalValue);
            if (outcome.DisplayChanged || IsPending(session) != wasPending)
            {
                changed.Add(snapshot.Id);
            }
        }

        return Effects([], changed);
    }

    public PanelEffects OnInput(string panelId, DeviceInputEvent input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!_panels.TryGetValue(panelId, out PanelView? view))
        {
            // It left, with this input still queued behind the leave.
            return PanelEffects.None;
        }

        // Acknowledge every input, even ignored ones, so the device can stop showing it as in flight.
        bool acked = input.Sequence > view.AckSequence;
        view.AckSequence = Math.Max(view.AckSequence, input.Sequence);
        PanelView? toAck = acked ? view : null;

        BridgeCommand? command = _actions.Resolve(input, new PanelState(view.Pages.Current));
        return command is null ? Effects([], [], toAck) : Execute(view, command, input, now, toAck);
    }

    /// <summary>Runs every session's timers: the write debounce, the forced write, the settle timeout.</summary>
    public PanelEffects OnTick(DateTimeOffset now)
    {
        List<SimCommand> sim = [];
        HashSet<ParameterId> changed = [];

        foreach (TuningSession session in _sessions.Values)
        {
            bool wasPending = IsPending(session);
            TuningOutcome outcome = session.Tick(now);
            if (outcome.WriteRequest is { } value)
            {
                sim.Add(new SimCommand.Write(session.Parameter.Id, value));
            }

            // A write changes nothing on screen; a rejection does, even back to the same value.
            if (outcome.DisplayChanged || IsPending(session) != wasPending)
            {
                changed.Add(session.Parameter.Id);
            }
        }

        return Effects(sim, changed);
    }

    private PanelEffects Execute(PanelView view, BridgeCommand command, DeviceInputEvent input, DateTimeOffset now, PanelView? toAck)
    {
        TuningSession tuned = _sessions[view.Pages.Current.Fields[0]];

        switch (command)
        {
            case BridgeCommand.AdjustValue adjust:
            {
                TimeSpan sinceLastTurn = view.LastTurnAt is { } last ? input.At - last : TimeSpan.MaxValue;
                view.LastTurnAt = input.At;

                TuningOutcome outcome = tuned.ApplyDetents(adjust.Detents, sinceLastTurn, now);
                return Effects(
                    outcome.WriteRequest is { } value ? [new SimCommand.Write(tuned.Parameter.Id, value)] : [],
                    outcome.DisplayChanged ? [tuned.Parameter.Id] : [],
                    toAck);
            }

            // The cursor belongs to the session, so every panel showing the value follows it.
            case BridgeCommand.CycleCursor:
                return Effects([], tuned.ToggleCursor().DisplayChanged ? [tuned.Parameter.Id] : [], toAck);

            case BridgeCommand.SwapSlots when view.Pages.Current.SwapEvent is { } swap:
            {
                // Send what the pilot has dialled before swapping it, or the sim swaps the old value.
                List<SimCommand> sim = [];
                if (tuned.Flush(now).WriteRequest is { } value)
                {
                    sim.Add(new SimCommand.Write(tuned.Parameter.Id, value));
                }

                tuned.AcceptNextSimValue();
                sim.Add(new SimCommand.Invoke(swap));
                return Effects(sim, [], toAck);
            }

            case BridgeCommand.NextPage:
                view.Pages.Next();
                return Redraw(view);

            case BridgeCommand.PreviousPage:
                view.Pages.Previous();
                return Redraw(view);

            case BridgeCommand.GoToPage go when view.Pages.TryGoTo(go.PageId):
                return Redraw(view);

            default:
                return Effects([], [], toAck);
        }
    }

    ParameterDefinition IPanelContext.Parameter(ParameterId id) => _sessions[id].Parameter;

    void IPanelContext.ReplaceGrid(ParameterId id, IValueGrid grid)
    {
        TuningSession session = _sessions[id];
        bool wasPending = IsPending(session);
        TuningOutcome outcome = session.ReplaceGrid(grid);
        if (outcome.DisplayChanged || IsPending(session) != wasPending)
        {
            _behaviourChanged.Add(id);
        }
    }

    /// <summary>
    /// The sim commands, a frame for every panel showing a changed parameter, and one for
    /// <paramref name="toAck"/> whatever it shows, since it has an input to acknowledge.
    /// </summary>
    private PanelEffects Effects(IReadOnlyList<SimCommand> sim, IReadOnlyCollection<ParameterId> changed, PanelView? toAck = null)
    {
        List<PanelFrame> frames = [];
        foreach (PanelView view in _panels.Values)
        {
            if (view == toAck || view.Pages.Current.Fields.Any(changed.Contains))
            {
                frames.Add(new PanelFrame(view.Id, Render(view)));
            }
        }

        return sim.Count == 0 && frames.Count == 0 ? PanelEffects.None : new PanelEffects(sim, frames);
    }

    private PanelEffects Redraw(PanelView view) => new([], [new PanelFrame(view.Id, Render(view))]);

    private DisplayFrame Render(PanelView view)
    {
        PanelPage page = view.Pages.Current;
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
            ++view.Revision,
            _sim,
            new PageDescriptor(page.Id, page.Title, page.Layout, view.Pages.Index, view.Pages.Count),
            fields,
            Notice: null,
            AckSequence: view.AckSequence);
    }

    private PanelView View(string panelId) =>
        _panels.TryGetValue(panelId, out PanelView? view)
            ? view
            : throw new InvalidOperationException($"Panel '{panelId}' has not joined.");

    private static bool IsPending(TuningSession session) => session.Status == PendingWriteStatus.AwaitingConfirmation;

    /// <summary>What belongs to one panel rather than to the sim: where it is, and what it has seen.</summary>
    private sealed class PanelView(string id, PageNavigator pages)
    {
        public string Id { get; } = id;

        public PageNavigator Pages { get; } = pages;

        public long Revision { get; set; }

        public long AckSequence { get; set; }

        public DateTimeOffset? LastTurnAt { get; set; }
    }
}

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

/// <summary>A frame for one device.</summary>
public sealed record DeviceFrame(string DeviceId, DisplayFrame Frame);

/// <summary>What one event requires of the outside world. Sim commands go before the renders.</summary>
public sealed record PanelEffects(IReadOnlyList<SimCommand> Sim, IReadOnlyList<DeviceFrame> Frames)
{
    public static PanelEffects None { get; } = new([], []);

    /// <summary>The frame for <paramref name="deviceId"/>, or null if it needs none.</summary>
    public DisplayFrame? FrameFor(string deviceId) => Frames.FirstOrDefault(f => f.DeviceId == deviceId)?.Frame;
}

/// <summary>
/// The panels' behaviour with the I/O taken out (spec §5.7, §6.2). One <see cref="TuningSession"/>
/// per parameter, shared by every device, so two devices showing COM 1 tune the same value rather
/// than fighting over it. Each device keeps its own page, revision, ack and turn timing, so devices
/// navigate independently. Each call takes one event and returns what to do about it, including
/// a frame for every device whose screen it changed; <see cref="PanelCoordinator"/> does it.
///
/// A device with no pages assigned shows the "unassigned" screen with the last six characters
/// of its hardware id, which the user copies into settings (§6.2). Its inputs are acknowledged
/// and otherwise ignored.
///
/// Pure and single-threaded, like the sessions it holds: time comes in as <c>now</c>, and the
/// coordinator's one event loop is the only caller.
/// </summary>
public sealed class PanelEngine : IPanelContext
{
    /// <summary>The page id of the screen an unassigned device shows.</summary>
    public const string UnassignedPageId = "unassigned";

    private static readonly FieldRole[] Roles = [FieldRole.Primary, FieldRole.Secondary, FieldRole.Tertiary];

    private readonly IReadOnlyDictionary<ParameterId, TuningSession> _sessions;
    private readonly IReadOnlyList<PanelPage> _pages;
    private readonly HashSet<string> _pageIds;
    private readonly IInputActionMap _actions;
    private readonly IReadOnlyList<IPanelBehaviour> _behaviours;
    private readonly Dictionary<string, DeviceView> _devices = new(StringComparer.Ordinal);

    // Parameters whose look changed while behaviours ran, so the devices showing them redraw.
    private readonly HashSet<ParameterId> _behaviourChanged = [];

    private SimConnectionState _sim = SimConnectionState.Disconnected;

    /// <param name="pages">Every page a device may be given (spec §6.2); a device that joins without an assignment gets them all.</param>
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
        _pageIds = [.. pages.Select(p => p.Id)];
        _actions = actions;
        _behaviours = behaviours ?? [];

        // Every registered parameter gets a session, on a page or not, so switching pages is
        // instant and values are current when a page appears (spec §5.7).
        _sessions = registry.All.ToDictionary(p => p.Id, p => new TuningSession(p, options));
    }

    public IReadOnlyCollection<string> Devices => _devices.Keys;

    /// <exception cref="InvalidOperationException">The device is unassigned, so it has no page.</exception>
    public PanelPage CurrentPage(string deviceId) =>
        (View(deviceId).Pages ?? throw new InvalidOperationException($"Device '{deviceId}' has no pages assigned.")).Current;

    /// <summary>The ids of the pages a device can show, in order; empty when unassigned.</summary>
    public IReadOnlyList<string> AssignedPages(string deviceId) => View(deviceId).PageIds;

    public TuningSession Session(ParameterId id) => _sessions[id];

    /// <summary>A device has connected. It starts on the first of its pages and gets its first frame.</summary>
    /// <param name="pages">What it shows: null for every page, empty for the unassigned screen.</param>
    public PanelEffects Join(string deviceId, IReadOnlyList<PanelPage>? pages = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        DeviceView view = new(deviceId);
        view.Assign(Checked(pages ?? _pages));
        if (!_devices.TryAdd(deviceId, view))
        {
            throw new InvalidOperationException($"Device '{deviceId}' has already joined.");
        }

        return Redraw(view);
    }

    /// <summary>
    /// Gives a connected device different pages, when its settings change. It stays on the page
    /// it was showing if it still has it. Nothing happens if the pages are the same.
    /// </summary>
    /// <param name="pages">Empty for the unassigned screen.</param>
    public PanelEffects Assign(string deviceId, IReadOnlyList<PanelPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        DeviceView view = View(deviceId);
        if (view.PageIds.SequenceEqual(pages.Select(p => p.Id), StringComparer.Ordinal))
        {
            return PanelEffects.None;
        }

        string? showing = view.Pages?.Current.Id;
        view.Assign(Checked(pages));
        if (showing is not null)
        {
            view.Pages?.TryGoTo(showing);
        }

        return Redraw(view);
    }

    private IReadOnlyList<PanelPage> Checked(IReadOnlyList<PanelPage> pages)
    {
        foreach (PanelPage page in pages.Where(p => !_pageIds.Contains(p.Id)))
        {
            throw new ArgumentException($"Page '{page.Id}' is not one of the engine's pages.", nameof(pages));
        }

        return pages;
    }

    /// <summary>A device has gone. Unknown ids are ignored, so leaving twice is harmless.</summary>
    public void Leave(string deviceId) => _devices.Remove(deviceId);

    /// <summary>
    /// A device restarted under an open port. It numbers its inputs from 1 again and has a blank
    /// screen, so the ack and the acceleration clock start over and it gets the current frame.
    /// It keeps its page: the pilot was on it a moment ago.
    /// </summary>
    public PanelEffects OnDeviceRestarted(string deviceId)
    {
        DeviceView view = View(deviceId);
        view.AckSequence = 0;
        view.LastTurnAt = null;
        return Redraw(view);
    }

    /// <summary>The frame for a device's current state, whether or not anything changed.</summary>
    public DisplayFrame Render(string deviceId) => Render(View(deviceId));

    public PanelEffects OnSimConnection(SimConnectionState state)
    {
        if (state == _sim)
        {
            return PanelEffects.None;
        }

        _sim = state;
        return new PanelEffects([], [.. _devices.Values.Select(v => new DeviceFrame(v.Id, Render(v)))]);
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

    public PanelEffects OnInput(string deviceId, DeviceInputEvent input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!_devices.TryGetValue(deviceId, out DeviceView? view))
        {
            // It left, with this input still queued behind the leave.
            return PanelEffects.None;
        }

        // Acknowledge every input, even ignored ones, so the device can stop showing it as in flight.
        bool acked = input.Sequence > view.AckSequence;
        view.AckSequence = Math.Max(view.AckSequence, input.Sequence);
        DeviceView? toAck = acked ? view : null;

        if (view.Pages is null)
        {
            return Effects([], [], toAck);
        }

        BridgeCommand? command = _actions.Resolve(input, new PanelState(view.Pages.Current));
        return command is null ? Effects([], [], toAck) : Execute(view, view.Pages, command, input, now, toAck);
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

    private PanelEffects Execute(DeviceView view, PageNavigator pages, BridgeCommand command, DeviceInputEvent input, DateTimeOffset now, DeviceView? toAck)
    {
        TuningSession tuned = _sessions[pages.Current.Fields[0]];

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

            // The cursor belongs to the session, so every device showing the value follows it.
            case BridgeCommand.CycleCursor:
                return Effects([], tuned.ToggleCursor().DisplayChanged ? [tuned.Parameter.Id] : [], toAck);

            case BridgeCommand.SwapSlots when pages.Current.SwapEvent is { } swap:
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
                pages.Next();
                return Redraw(view);

            case BridgeCommand.PreviousPage:
                pages.Previous();
                return Redraw(view);

            case BridgeCommand.GoToPage go when pages.TryGoTo(go.PageId):
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
    /// The sim commands, a frame for every device showing a changed parameter, and one for
    /// <paramref name="toAck"/> whatever it shows, since it has an input to acknowledge.
    /// </summary>
    private PanelEffects Effects(IReadOnlyList<SimCommand> sim, IReadOnlyCollection<ParameterId> changed, DeviceView? toAck = null)
    {
        List<DeviceFrame> frames = [];
        foreach (DeviceView view in _devices.Values)
        {
            if (view == toAck || view.Pages?.Current.Fields.Any(changed.Contains) == true)
            {
                frames.Add(new DeviceFrame(view.Id, Render(view)));
            }
        }

        return sim.Count == 0 && frames.Count == 0 ? PanelEffects.None : new PanelEffects(sim, frames);
    }

    private PanelEffects Redraw(DeviceView view) => new([], [new DeviceFrame(view.Id, Render(view))]);

    private DisplayFrame Render(DeviceView view)
    {
        if (view.Pages is not { } pages)
        {
            return RenderUnassigned(view);
        }

        PanelPage page = pages.Current;
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
            new PageDescriptor(page.Id, page.Title, page.Layout, pages.Index, pages.Count),
            fields,
            Notice: null,
            AckSequence: view.AckSequence);
    }

    /// <summary>
    /// A page the firmware already draws: the title, then the last six characters of the
    /// hardware id as the value, the same six the waiting screen shows and settings accept.
    /// </summary>
    private DisplayFrame RenderUnassigned(DeviceView view) => new(
        ++view.Revision,
        _sim,
        new PageDescriptor(UnassignedPageId, "NOT ASSIGNED", PageLayout.SingleValue, Index: 0, Count: 1),
        [new FieldDescriptor(FieldRole.Primary, "ADD TO SETTINGS", DeviceIdentity.ShortIdOf(view.Id))],
        Notice: null,
        AckSequence: view.AckSequence);

    private DeviceView View(string deviceId) =>
        _devices.TryGetValue(deviceId, out DeviceView? view)
            ? view
            : throw new InvalidOperationException($"Device '{deviceId}' has not joined.");

    private static bool IsPending(TuningSession session) => session.Status == PendingWriteStatus.AwaitingConfirmation;

    /// <summary>What belongs to one device rather than to the sim: where it is, and what it has seen.</summary>
    private sealed class DeviceView(string id)
    {
        public string Id { get; } = id;

        /// <summary>Null while the device is unassigned.</summary>
        public PageNavigator? Pages { get; private set; }

        public IReadOnlyList<string> PageIds { get; private set; } = [];

        public void Assign(IReadOnlyList<PanelPage> pages)
        {
            Pages = pages.Count == 0 ? null : new PageNavigator(pages);
            PageIds = [.. pages.Select(p => p.Id)];
        }

        public long Revision { get; set; }

        public long AckSequence { get; set; }

        public DateTimeOffset? LastTurnAt { get; set; }
    }
}

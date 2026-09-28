namespace Crowsnest.Core.Domain.Tuning;

public enum PendingWriteStatus
{
    /// <summary>Nothing outstanding; the display shows the sim's value.</summary>
    None,

    /// <summary>The pilot has moved the value and the sim has not yet agreed to it.</summary>
    AwaitingConfirmation,

    /// <summary>The sim reported the value we wrote.</summary>
    Confirmed,

    /// <summary>The sim never reported our value within the settle timeout; the display went back to the sim's.</summary>
    Rejected,
}

/// <summary>What one call changed, for the caller to act on.</summary>
/// <param name="DisplayChanged">The panel needs redrawing: the value, the cursor, or both.</param>
/// <param name="WriteRequest">A value to send to the sim now, or null.</param>
public readonly record struct TuningOutcome(bool DisplayChanged, int? WriteRequest, PendingWriteStatus Status);

/// <summary>
/// One parameter being tuned: the value on the panel, the value the sim last agreed to, and the
/// writes that reconcile the two (spec §5.4).
///
/// Pure: no clock, no I/O, not thread-safe. Time comes in as <c>now</c>, and the caller sends
/// any <see cref="TuningOutcome.WriteRequest"/> and calls <see cref="Tick"/> often enough for
/// the debounce and settle timers to fire (every 20-50 ms is plenty).
///
/// The reconciliation rule: while a pending value exists, sim values are ignored unless they
/// match what we wrote or what the pilot has dialled (→ <see cref="PendingWriteStatus.Confirmed"/>).
/// A write the sim has not confirmed within <see cref="TuningOptions.SettleTimeout"/> is given
/// up on, and the display reverts to the latest value the sim reported
/// (→ <see cref="PendingWriteStatus.Rejected"/>). This is what lets the sim move a value itself,
/// as it does at flight load and as VNAV does with the autopilot altitude: the session never
/// fights it for longer than the timeout.
/// </summary>
public sealed class TuningSession
{
    private readonly TuningOptions _options;

    private int _confirmed;
    private int _latestSim;
    private int? _pending;

    // The last value written for the current pending value, or null if none has been.
    private int? _written;
    private DateTimeOffset _writtenAt;

    // True while the pending value differs from what was last written.
    private bool _dirty;
    private DateTimeOffset _dirtySince;
    private DateTimeOffset _lastDetentAt;

    private int _cursorIndex;

    public TuningSession(ParameterDefinition parameter, TuningOptions options)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(options);

        Parameter = parameter;
        _options = options;
    }

    public ParameterDefinition Parameter { get; private set; }

    /// <summary>
    /// False until the sim has reported a value. Until then there is nothing to show or step
    /// from, and detents are ignored.
    /// </summary>
    public bool HasValue { get; private set; }

    /// <summary>The pending value if there is one, else the confirmed value.</summary>
    public int Displayed => _pending ?? _confirmed;

    /// <summary>
    /// The last value the sim agreed to. Shown as the sim reported it, even when it is not on
    /// the grid: the first detent snaps it (decided 2026-09-27, spec §5.3).
    /// </summary>
    public int Confirmed => _confirmed;

    public CursorLevel Cursor => Parameter.Cursors[_cursorIndex];

    public PendingWriteStatus Status { get; private set; }

    public TuningOutcome ApplyDetents(int detents, TimeSpan sinceLastDetent, DateTimeOffset now)
    {
        if (!HasValue)
        {
            return Unchanged();
        }

        int steps = _options.Acceleration.Scale(detents, sinceLastDetent);
        if (steps == 0)
        {
            return Unchanged();
        }

        _lastDetentAt = now;

        int before = Displayed;
        int next = Parameter.Grid.Step(before, steps, Cursor);
        if (next == before)
        {
            // Stopped at the end of the range: nothing to show or write.
            return Unchanged();
        }

        if (next == _confirmed && _written is null)
        {
            // Turned back to where it started before anything went to the sim.
            ClearPending();
            Status = PendingWriteStatus.None;
            return new TuningOutcome(true, null, Status);
        }

        _pending = next;
        Status = PendingWriteStatus.AwaitingConfirmation;

        bool dirty = next != _written;
        if (dirty && !_dirty)
        {
            _dirtySince = now;
        }

        _dirty = dirty;

        int? write = _dirty && now - _dirtySince >= _options.MaxWriteInterval ? Write(now) : null;
        return new TuningOutcome(true, write, Status);
    }

    /// <summary>Moves to the next cursor, finer first and then back round to the coarsest.</summary>
    public TuningOutcome ToggleCursor()
    {
        if (Parameter.Cursors.Count == 1)
        {
            return Unchanged();
        }

        _cursorIndex = (_cursorIndex + 1) % Parameter.Cursors.Count;
        return new TuningOutcome(true, null, Status);
    }

    public TuningOutcome ObserveSimValue(int value)
    {
        _latestSim = value;
        int before = Displayed;

        if (!HasValue)
        {
            HasValue = true;
            _confirmed = value;
            return new TuningOutcome(true, null, Status);
        }

        if (_pending is null)
        {
            if (value != _confirmed)
            {
                // Someone else moved it: the cockpit knob, ATC, the autopilot.
                _confirmed = value;
                Status = PendingWriteStatus.None;
            }
        }
        else if (value == _pending)
        {
            _confirmed = value;
            ClearPending();
            Status = PendingWriteStatus.Confirmed;
        }
        else if (value == _written)
        {
            // The sim took an earlier write of this spin; the pilot has already moved on.
            _confirmed = value;
        }

        return new TuningOutcome(Displayed != before, null, Status);
    }

    /// <summary>Fires the write debounce, the forced write during a long spin, and the settle timeout.</summary>
    public TuningOutcome Tick(DateTimeOffset now)
    {
        if (_dirty)
        {
            bool quiet = now - _lastDetentAt >= _options.WriteDebounce;
            bool overdue = now - _dirtySince >= _options.MaxWriteInterval;
            return quiet || overdue ? new TuningOutcome(false, Write(now), Status) : Unchanged();
        }

        if (_pending is not null && _written is not null && now - _writtenAt >= _options.SettleTimeout)
        {
            int before = Displayed;
            _confirmed = _latestSim;
            ClearPending();
            Status = PendingWriteStatus.Rejected;
            return new TuningOutcome(Displayed != before, null, Status);
        }

        return Unchanged();
    }

    /// <summary>
    /// Swaps the legal values, as COM does when the aircraft changes spacing mode (spec §5.3).
    /// A pending value is dropped rather than snapped: the sim snaps its own values on a mode
    /// change and reports them, and a value the pilot dialled on the old grid may be illegal on
    /// the new one. The confirmed value stays as the sim reported it until the sim says otherwise.
    /// </summary>
    public TuningOutcome ReplaceGrid(Grids.IValueGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        Parameter = Parameter with { Grid = grid };
        if (_pending is null)
        {
            return Unchanged();
        }

        int before = Displayed;
        ClearPending();
        Status = PendingWriteStatus.None;
        return new TuningOutcome(Displayed != before, null, Status);
    }

    /// <summary>
    /// Writes the pending value now if it has not been written, without waiting for the
    /// debounce. For a swap, which must exchange what the pilot sees rather than what the sim
    /// last had.
    /// </summary>
    public TuningOutcome Flush(DateTimeOffset now) =>
        _dirty ? new TuningOutcome(false, Write(now), Status) : Unchanged();

    private int Write(DateTimeOffset now)
    {
        int value = _pending!.Value;
        _written = value;
        _writtenAt = now;
        _dirty = false;
        return value;
    }

    private void ClearPending()
    {
        _pending = null;
        _written = null;
        _dirty = false;
    }

    private TuningOutcome Unchanged() => new(false, null, Status);
}

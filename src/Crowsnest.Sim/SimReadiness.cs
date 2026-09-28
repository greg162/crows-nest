namespace Crowsnest.Sim;

/// <summary>
/// Decides when the sim's values can be trusted (spec §5.3, "Rule for Crowsnest.Sim"):
/// only once <c>CAMERA STATE</c> is a flying view and nothing has changed on its own for a
/// quiet period. Before that, the sim and the aircraft are still fighting over the radios: the
/// TriStar's last unprompted write came 1.4 s after the cockpit view appeared.
///
/// Pure: time comes in as <c>now</c>. The gateway feeds it every camera report and every value
/// change, and asks <see cref="Check"/> regularly.
/// </summary>
public sealed class SimReadiness(TimeSpan quiet)
{
    /// <summary>The spec's figure: 3 s passed in every run, the TriStar's margin was 1.6 s.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(5);

    // Observed in MSFS 2024: 2 cockpit, 3 external, 4 showcase. Not flying: 12 aircraft
    // selection, 16 Start Flight, 30 fly-in, 32/35 menus and loading, 0 in transitions.
    private static readonly HashSet<int> FlyingCameras = [2, 3, 4];

    // 29 is the in-flight Esc (pause) menu, seen 2026-09-27. It reloads nothing, so it is
    // ignored: everything carries on as if the previous view were still showing. Anything
    // reached from it that does reload (restart, main menu) passes through the loading cameras,
    // which end readiness as usual.
    private static readonly HashSet<int> NeutralCameras = [29];

    private bool _flying;
    private DateTimeOffset _quietSince;

    public TimeSpan Quiet { get; } = quiet;

    public bool IsReady { get; private set; }

    /// <summary>The session opened. Nothing is trusted until the camera says so.</summary>
    public void Reset(DateTimeOffset now)
    {
        _flying = false;
        _quietSince = now;
        IsReady = false;
    }

    /// <returns>True if readiness changed (it can only be lost here, never gained).</returns>
    public bool OnCamera(int state, DateTimeOffset now)
    {
        if (NeutralCameras.Contains(state))
        {
            return false;
        }

        bool flying = FlyingCameras.Contains(state);
        if (flying && !_flying)
        {
            // The quiet period counts from the handover, not from the last change before it.
            _quietSince = now;
        }

        _flying = flying;
        if (!flying && IsReady)
        {
            // Back to the menu, or a new flight loading: the fight over the radios starts again.
            IsReady = false;
            return true;
        }

        return false;
    }

    /// <summary>A value changed without us writing it. Only matters before readiness.</summary>
    public void OnUnsolicitedChange(DateTimeOffset now)
    {
        if (!IsReady)
        {
            _quietSince = now;
        }
    }

    /// <returns>True if the sim has just become ready.</returns>
    public bool Check(DateTimeOffset now)
    {
        if (IsReady || !_flying || now - _quietSince < Quiet)
        {
            return false;
        }

        IsReady = true;
        return true;
    }
}

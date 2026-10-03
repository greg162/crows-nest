namespace Crowsnest.Core.Domain.Tuning;

/// <summary>
/// Turns raw encoder detents into grid steps, so a fast spin covers more ground (spec §5.4).
/// Implementations must be pure: the same inputs always give the same result.
/// </summary>
public interface IEncoderAcceleration
{
    /// <param name="detents">Signed clicks since the last report.</param>
    /// <param name="sinceLastDetent">Time since the previous report, from the input events' timestamps (today the host's receive time).</param>
    /// <returns>Signed steps to apply. Must have the same sign as <paramref name="detents"/>, or be 0.</returns>
    int Scale(int detents, TimeSpan sinceLastDetent);
}

/// <summary>One detent, one step. The default until there is a real knob to tune a curve against.</summary>
public sealed class NoAcceleration : IEncoderAcceleration
{
    public static NoAcceleration Instance { get; } = new();

    private NoAcceleration()
    {
    }

    public int Scale(int detents, TimeSpan sinceLastDetent) => detents;
}

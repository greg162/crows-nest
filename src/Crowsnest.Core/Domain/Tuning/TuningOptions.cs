namespace Crowsnest.Core.Domain.Tuning;

/// <summary>Timing for a <see cref="TuningSession"/> (spec §5.4).</summary>
/// <param name="WriteDebounce">Quiet time after the last detent before the value is written.</param>
/// <param name="MaxWriteInterval">
/// Longest a spin goes without a write, so the sim follows a long turn instead of waiting for it
/// to stop.
/// </param>
/// <param name="SettleTimeout">
/// How long a write may go unconfirmed before the session gives up and shows the sim's value.
/// The 2026-09-26 aircraft matrix saw read-backs up to 586 ms just after flight load, so this
/// must stay well above 600 ms.
/// </param>
public sealed record TuningOptions(
    TimeSpan WriteDebounce,
    TimeSpan MaxWriteInterval,
    TimeSpan SettleTimeout,
    IEncoderAcceleration Acceleration)
{
    public static TuningOptions Default { get; } = new(
        WriteDebounce: TimeSpan.FromMilliseconds(120),
        MaxWriteInterval: TimeSpan.FromMilliseconds(300),
        SettleTimeout: TimeSpan.FromMilliseconds(1500),
        Acceleration: NoAcceleration.Instance);
}

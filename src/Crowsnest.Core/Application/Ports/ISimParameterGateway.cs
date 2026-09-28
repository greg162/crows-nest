using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Application.Ports;

/// <summary>
/// The simulator, as Core sees it (spec §5.6). Core knows nothing of SimConnect; values cross
/// this boundary already in their canonical unit.
/// </summary>
public interface ISimParameterGateway : IAsyncDisposable
{
    IObservable<SimConnectionState> ConnectionState { get; }

    /// <summary>
    /// Every value change for the subscribed parameters, in the order the sim reported them.
    /// Values the gateway knows are not real (a COM frequency of 0 Hz mid-load, spec §5.3)
    /// arrive with <see cref="ParameterSnapshot.Available"/> false or not at all.
    /// </summary>
    IAsyncEnumerable<ParameterSnapshot> Snapshots { get; }

    /// <summary>Subscribes to every value, parameters and watches alike; snapshots come back under each subscription's id.</summary>
    Task SubscribeAsync(IReadOnlyList<SimSubscription> subscriptions, CancellationToken ct);

    Task WriteAsync(ParameterId id, int canonicalValue, CancellationToken ct);

    /// <summary>A key event with no value of its own: swaps, toggles.</summary>
    Task InvokeAsync(string eventName, uint payload, CancellationToken ct);
}

/// <param name="Available">False when the sim has no meaningful value for this parameter right now.</param>
public sealed record ParameterSnapshot(ParameterId Id, int CanonicalValue, bool Available = true);

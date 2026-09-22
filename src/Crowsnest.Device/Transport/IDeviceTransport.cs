using System.IO.Pipelines;

namespace Crowsnest.Device.Transport;

/// <summary>
/// A byte pipe to one panel (spec §6). Serial today, WebSocket in phase 7, loopback in tests —
/// everything above this interface is transport-agnostic, including the OTA path.
/// </summary>
public interface IDeviceTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);

    PipeReader Input { get; }

    PipeWriter Output { get; }

    IObservable<bool> IsConnected { get; }
}

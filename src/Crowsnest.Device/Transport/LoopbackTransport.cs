using System.IO.Pipelines;
using Crowsnest.Device.Internal;

namespace Crowsnest.Device.Transport;

/// <summary>
/// Two transports wired mouth-to-ear, for tests and for Crowsnest.DeviceSimulator.
/// The host end's writes surface on the device end's reads, and vice versa.
/// </summary>
public sealed class LoopbackTransport : IDeviceTransport
{
    private readonly Pipe _inbound;
    private readonly Pipe _outbound;
    private readonly BehaviorSubject<bool> _connected = new(false);

    private LoopbackTransport(Pipe inbound, Pipe outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    /// <summary>Creates a connected pair. Both ends still need <see cref="ConnectAsync"/>.</summary>
    public static (LoopbackTransport Host, LoopbackTransport Device) CreatePair()
    {
        var hostToDevice = new Pipe();
        var deviceToHost = new Pipe();
        return (new LoopbackTransport(deviceToHost, hostToDevice),
                new LoopbackTransport(hostToDevice, deviceToHost));
    }

    public PipeReader Input => _inbound.Reader;

    public PipeWriter Output => _outbound.Writer;

    public IObservable<bool> IsConnected => _connected;

    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _connected.OnNext(true);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connected.OnNext(false);
        _connected.OnCompleted();
        _outbound.Writer.Complete();
        _inbound.Reader.Complete();
        return ValueTask.CompletedTask;
    }
}

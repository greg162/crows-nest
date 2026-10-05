using Crowsnest.Device.Transport;

namespace Crowsnest.Device.Simulation;

/// <summary>
/// Pretend USB ports for <see cref="DeviceManager"/>: plug a <see cref="SimulatedDevice"/> into
/// a port, or something that never answers, and pull it out again. Pass <see cref="FindPorts"/>
/// and <see cref="Open"/> in <see cref="DeviceManagerOptions"/>. Each open starts a fresh
/// device on the far end of a loopback, as a real board answers each new connection.
/// </summary>
public sealed class SimulatedUsb : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string?> _plugged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Plugged> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _opens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A board with this hardware id is now on <paramref name="port"/>.</summary>
    public void Plug(string port, string hardwareId)
    {
        ArgumentException.ThrowIfNullOrEmpty(hardwareId);

        lock (_gate)
        {
            _plugged[port] = hardwareId;
        }
    }

    /// <summary>Something on <paramref name="port"/> that has our USB id but never says hello.</summary>
    public void PlugSilent(string port)
    {
        lock (_gate)
        {
            _plugged[port] = null;
        }
    }

    /// <summary>Pulls the cable: the port goes, and an open link to it ends.</summary>
    public async Task UnplugAsync(string port)
    {
        Plugged? open;
        lock (_gate)
        {
            _plugged.Remove(port);
            _open.Remove(port, out open);
        }

        if (open is not null)
        {
            await open.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The device answering on <paramref name="port"/> now, if any.</summary>
    public SimulatedDevice? Device(string port)
    {
        lock (_gate)
        {
            return _open.GetValueOrDefault(port)?.Device;
        }
    }

    /// <summary>How many times <paramref name="port"/> has been opened.</summary>
    public int Opens(string port)
    {
        lock (_gate)
        {
            return _opens.GetValueOrDefault(port);
        }
    }

    public IReadOnlyList<string> FindPorts()
    {
        lock (_gate)
        {
            return [.. _plugged.Keys];
        }
    }

    public IDeviceTransport Open(string port)
    {
        (LoopbackTransport host, LoopbackTransport far) = LoopbackTransport.CreatePair();
        Plugged? previous;
        lock (_gate)
        {
            if (!_plugged.TryGetValue(port, out string? hardwareId))
            {
                throw new IOException($"{port} is not plugged in.");
            }

            _opens[port] = _opens.GetValueOrDefault(port) + 1;
            _open.Remove(port, out previous);
            _open[port] = new Plugged(far, hardwareId is null ? null : new SimulatedDevice(far, hardwareId));
        }

        _ = previous?.CloseAsync();
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        List<Plugged> open;
        lock (_gate)
        {
            open = [.. _open.Values];
            _open.Clear();
            _plugged.Clear();
        }

        foreach (Plugged plugged in open)
        {
            await plugged.CloseAsync().ConfigureAwait(false);
        }
    }

    private sealed class Plugged
    {
        private readonly LoopbackTransport _far;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Plugged(LoopbackTransport far, SimulatedDevice? device)
        {
            _far = far;
            Device = device;
            _loop = device?.RunAsync(_stop.Token) ?? Task.CompletedTask;
        }

        public SimulatedDevice? Device { get; }

        public async Task CloseAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException)
            {
                // Its link was torn down underneath it.
            }

            // Completes the far side of the pipe: the host reads end of stream.
            await _far.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}

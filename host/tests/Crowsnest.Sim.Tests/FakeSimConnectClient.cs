using System.Collections.Concurrent;
using System.Threading.Channels;
using Crowsnest.SimConnect;

namespace Crowsnest.Sim.Tests;

/// <summary>A SimConnect session the test scripts: it records what was asked of it and delivers what the test pushes.</summary>
internal sealed class FakeSimConnectClient : ISimConnectClient
{
    private readonly Channel<SimConnectMessage> _messages = Channel.CreateUnbounded<SimConnectMessage>();

    /// <summary>When true, <see cref="OpenAsync"/> fails as it does with no sim running.</summary>
    public bool Unavailable { get; init; }

    public bool Opened { get; private set; }

    public bool Disposed { get; private set; }

    public ConcurrentQueue<(uint Id, string SimVar, string Unit, SimConnectPeriod Period)> Watches { get; } = new();

    public ConcurrentQueue<(string Event, uint Data)> Transmits { get; } = new();

    public ChannelReader<SimConnectMessage> Messages => _messages.Reader;

    public void Push(SimConnectMessage message) => _messages.Writer.TryWrite(message);

    /// <summary>A value for the SimVar watched under this name.</summary>
    public void Push(string simVar, double raw) =>
        Push(new SimConnectMessage.Value(Watches.Single(w => w.SimVar == simVar).Id, raw));

    public Task OpenAsync(string clientName, CancellationToken ct)
    {
        if (Unavailable)
        {
            throw new SimConnectUnavailableException("no sim");
        }

        Opened = true;
        return Task.CompletedTask;
    }

    public void WatchValue(uint id, string simVar, string unit, SimConnectPeriod period) => Watches.Enqueue((id, simVar, unit, period));

    public void TransmitEvent(string eventName, uint data) => Transmits.Enqueue((eventName, data));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

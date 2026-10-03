using System.Threading.Channels;

namespace Crowsnest.SimConnect;

/// <summary>
/// One SimConnect session (spec §7.1), cut down to what the gateway uses: watch a SimVar,
/// send a key event, hear what comes back. A client is single-use: after <see cref="SimConnectMessage.Quit"/>
/// or disposal, make a new one.
///
/// Calls are safe from any thread; implementations hand them to the thread that owns the session.
/// </summary>
public interface ISimConnectClient : IAsyncDisposable
{
    /// <exception cref="SimConnectUnavailableException">The sim is not running, or refused the connection.</exception>
    Task OpenAsync(string clientName, CancellationToken ct);

    /// <summary>
    /// Watches one SimVar as FLOAT64 on the user aircraft, reported under <paramref name="id"/>
    /// whenever it changes (SimConnect's <c>CHANGED</c> flag) at <paramref name="period"/>.
    /// </summary>
    void WatchValue(uint id, string simVar, string unit, SimConnectPeriod period);

    /// <summary>Sends a key event to the user aircraft at the highest group priority, mapping the name on first use.</summary>
    void TransmitEvent(string eventName, uint data);

    ChannelReader<SimConnectMessage> Messages { get; }
}

public enum SimConnectPeriod
{
    Once,
    VisualFrame,
    SimFrame,
    Second,
}

public abstract record SimConnectMessage
{
    private SimConnectMessage()
    {
    }

    public sealed record Opened(string ApplicationName, string Version) : SimConnectMessage;

    /// <summary>The sim is shutting down. The client is finished.</summary>
    public sealed record Quit : SimConnectMessage;

    /// <summary>
    /// SimConnect rejected something we sent: an unknown event name, a bad SimVar.
    /// <paramref name="SendId"/> identifies the call; <paramref name="Exception"/> is the SIMCONNECT_EXCEPTION name.
    /// </summary>
    public sealed record Failed(string Exception, uint SendId, uint Index) : SimConnectMessage;

    public sealed record Value(uint Id, double Raw) : SimConnectMessage;
}

/// <summary>The sim is not running or would not accept a connection. Expected; retry later.</summary>
public sealed class SimConnectUnavailableException : Exception
{
    public SimConnectUnavailableException()
    {
    }

    public SimConnectUnavailableException(string message)
        : base(message)
    {
    }

    public SimConnectUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

namespace Crowsnest.Core.Application.Ports;

/// <summary>State of the link to the simulator, surfaced to the devices so they can say so.</summary>
public enum SimConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

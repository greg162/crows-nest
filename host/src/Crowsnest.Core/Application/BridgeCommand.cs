using Crowsnest.Core.Application.Ports;

namespace Crowsnest.Core.Application;

/// <summary>What the pilot asked for, once a gesture has been interpreted (spec §5.7).</summary>
public abstract record BridgeCommand
{
    private BridgeCommand()
    {
    }

    public sealed record AdjustValue(int Detents) : BridgeCommand;

    public sealed record CycleCursor : BridgeCommand;

    /// <summary>No-op on a page without a swap event.</summary>
    public sealed record SwapSlots : BridgeCommand;

    public sealed record NextPage : BridgeCommand;

    public sealed record PreviousPage : BridgeCommand;

    public sealed record GoToPage(string PageId) : BridgeCommand;
}

/// <summary>What an <see cref="IInputActionMap"/> may consult: the page the gesture landed on.</summary>
public sealed record PanelState(PanelPage Page);

/// <summary>Turns a gesture into a command, or null to ignore it. Rebinding gestures is swapping this.</summary>
public interface IInputActionMap
{
    BridgeCommand? Resolve(DeviceInputEvent input, PanelState state);
}

/// <summary>
/// The v1 bindings (spec §5.7): turn adjusts, short press cycles the cursor, tap swaps, long
/// press or a left swipe goes to the next page, a right swipe to the previous one.
/// </summary>
public sealed class DefaultInputActionMap : IInputActionMap
{
    public static DefaultInputActionMap Instance { get; } = new();

    private DefaultInputActionMap()
    {
    }

    public BridgeCommand? Resolve(DeviceInputEvent input, PanelState state) => input switch
    {
        DeviceInputEvent.EncoderTurned turned => new BridgeCommand.AdjustValue(turned.Detents),
        DeviceInputEvent.KnobPressed { Kind: PressKind.Short } => new BridgeCommand.CycleCursor(),
        DeviceInputEvent.KnobPressed { Kind: PressKind.Long } => new BridgeCommand.NextPage(),
        DeviceInputEvent.ScreenTapped => new BridgeCommand.SwapSlots(),
        DeviceInputEvent.SwipeDetected { Dir: SwipeDir.Left } => new BridgeCommand.NextPage(),
        DeviceInputEvent.SwipeDetected { Dir: SwipeDir.Right } => new BridgeCommand.PreviousPage(),
        _ => null,
    };
}

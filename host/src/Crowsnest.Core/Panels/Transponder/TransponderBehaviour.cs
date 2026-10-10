using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Panels.Transponder;

/// <summary>
/// The transponder's mode and IDENT (spec §5.8). The mode is shown, never set: a swipe that
/// put the transponder in STBY by accident would hide the aircraft from ATC, so it is changed
/// in the cockpit. A tap sends IDENT, which costs little if it is an accident. The caption
/// under the code reads the mode, and "IDENT", lit, for as long as the sim says ident is on;
/// the sim ends it, not a timer here.
/// </summary>
public sealed class TransponderBehaviour : IPanelBehaviour
{
    public const string PageId = "xpdr";
    public const string IdentEvent = "XPNDR_IDENT_ON";

    public static readonly ParameterId Mode = new("xpdr.mode");
    public static readonly ParameterId Ident = new("xpdr.ident");

    // TRANSPONDER STATE (Enum), as the SDK numbers it.
    private static readonly string[] ModeNames = ["OFF", "STBY", "TEST", "ON", "ALT", "GND"];

    private string? _mode;
    private bool _ident;

    public void Validate(ParameterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (ParameterId watch in new[] { Mode, Ident }.Where(id => !registry.Watches.Any(w => w.Id == id)))
        {
            throw new InvalidDataException($"The transponder needs the watch '{watch}', which is not registered.");
        }
    }

    public void OnSnapshot(ParameterSnapshot snapshot, IPanelContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        if (snapshot.Id == Mode)
        {
            _mode = snapshot.Available && snapshot.CanonicalValue >= 0 && snapshot.CanonicalValue < ModeNames.Length
                ? ModeNames[snapshot.CanonicalValue]
                : null;
        }
        else if (snapshot.Id == Ident)
        {
            _ident = snapshot.Available && snapshot.CanonicalValue != 0;
        }
        else
        {
            return;
        }

        context.SetCaption(PageId, Caption());
    }

    /// <summary>A tap on the transponder's page, which has no standby code to swap, is IDENT.</summary>
    public void OnCommand(BridgeCommand command, PanelPage page, IPanelContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        if (command is BridgeCommand.SwapSlots && page.Id == PageId)
        {
            context.Invoke(IdentEvent);
        }
    }

    private PageCaption? Caption() => (_mode, _ident) switch
    {
        (null, false) => null,
        (null, true) => new PageCaption("IDENT", Lit: true),
        (_, true) => new PageCaption($"{_mode}   IDENT", Lit: true),
        _ => new PageCaption(_mode),
    };
}

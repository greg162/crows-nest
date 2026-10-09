using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Panels.Com;
using Crowsnest.Core.Panels.Nav;
using Crowsnest.Core.Panels.Transponder;

namespace Crowsnest.Core.Panels;

/// <summary>
/// Every panel the host ships (spec §5.8). Adding a panel is its folder under <c>Panels/</c>
/// plus one line here; see <c>Panels/README.md</c>. Page order follows this list.
///
/// An explicit list rather than assembly scanning: it starts fast, survives trimming, and
/// "why is this page here?" is answered by reading it. A test fails if a module exists but is
/// missing from the list.
/// </summary>
public static class PanelCatalog
{
    /// <summary>New instances on every call, so each load gets its own behaviours.</summary>
    public static IReadOnlyList<IPanelModule> All =>
    [
        new ComPanelModule(),
        new NavPanelModule(),
        new TransponderPanelModule(),
    ];

    public static PanelSetup Load() => PanelComposer.Compose(All);
}

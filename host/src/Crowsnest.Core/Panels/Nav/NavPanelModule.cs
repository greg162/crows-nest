using Crowsnest.Core.Application.Panels;

namespace Crowsnest.Core.Panels.Nav;

/// <summary>
/// NAV 1 (spec §5.8). Pure data: everything is in <c>nav.parameters.json</c>. It has no view
/// yet, so it is registered but shows no page until phase 5 (spec §13).
/// </summary>
public sealed class NavPanelModule : IPanelModule
{
    public string Id => "nav";

    public void Configure(PanelBuilder panel)
    {
    }
}

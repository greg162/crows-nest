using Crowsnest.Core.Application.Panels;

namespace Crowsnest.Core.Panels.Nav;

/// <summary>
/// NAV 1 (spec §5.8). Pure data: the parameters are in <c>nav.parameters.json</c> and the page
/// in <c>View/nav.view.json</c>, so there is nothing to configure.
/// </summary>
public sealed class NavPanelModule : IPanelModule
{
    public string Id => "nav";

    public void Configure(PanelBuilder panel)
    {
    }
}

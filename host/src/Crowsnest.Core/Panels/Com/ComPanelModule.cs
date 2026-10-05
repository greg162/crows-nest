using Crowsnest.Core.Application.Panels;

namespace Crowsnest.Core.Panels.Com;

/// <summary>
/// COM 1 and COM 2 (spec §5.8): the parameters and the spacing watches in
/// <c>com.parameters.json</c>, the pages in <c>View/com.view.json</c>. The grid follows the
/// aircraft's 25 / 8.33 kHz spacing mode, which plain data cannot do.
/// </summary>
public sealed class ComPanelModule : IPanelModule
{
    public string Id => "com";

    public void Configure(PanelBuilder panel)
    {
        ComSpacingBehaviour spacing = new();
        panel.AddGridType(ComSpacingBehaviour.GridType, spacing.GridFactory)
             .AddBehaviour(spacing);
    }
}

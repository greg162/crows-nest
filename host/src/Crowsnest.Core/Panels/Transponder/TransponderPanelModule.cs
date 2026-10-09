using Crowsnest.Core.Application.Panels;

namespace Crowsnest.Core.Panels.Transponder;

/// <summary>
/// The transponder's squawk code (spec §5.8): the parameter in <c>xpdr.parameters.json</c>, the
/// page in <c>View/xpdr.view.json</c>. The code is four octal digits, which needs the
/// <see cref="DigitGrid"/>; a short press moves the cursor from digit to digit.
/// </summary>
public sealed class TransponderPanelModule : IPanelModule
{
    /// <summary>The grid's type in the parameters file.</summary>
    public const string DigitsGridType = "digits";

    public string Id => "xpdr";

    public void Configure(PanelBuilder panel) =>
        panel.AddGridType(DigitsGridType, spec => new DigitGrid(spec.RequireInt("digits"), spec.RequireInt("radix")));
}

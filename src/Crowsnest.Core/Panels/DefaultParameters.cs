using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Panels;

/// <summary>
/// What the host ships with: every <c>*.parameters.json</c> embedded under <c>Panels/</c>, read
/// with the standard grids and formatters plus the ones panels own, the pages, and the panel
/// behaviours.
///
/// A stand-in for the panel modules (spec §5.8): when <c>IPanelModule</c> and
/// <c>PanelBuilder</c> arrive, each module contributes its own JSON, grids, formatters, pages
/// and behaviours, and this class goes away. Until then, a panel registers them here.
/// </summary>
public static class DefaultParameters
{
    public static IReadOnlyList<string> ResourceNames { get; } =
    [
        "Crowsnest.Core.Panels.Com.com.parameters.json",
        "Crowsnest.Core.Panels.Nav.nav.parameters.json",
    ];

    /// <summary>
    /// The pages the host ships with: COM 1 only. NAV 1 is registered but has no page until
    /// phase 5 (spec §13).
    /// </summary>
    public static IReadOnlyList<PanelPage> Pages { get; } =
    [
        new PanelPage("com1", "COM 1", PageLayout.ActiveStandbyPair,
            [new ParameterId("com1.standby"), new ParameterId("com1.active")],
            SwapEvent: "COM_STBY_RADIO_SWAP"),
    ];

    /// <summary>A reader with every grid type and formatter the shipped panels use.</summary>
    public static ParameterJsonReader CreateReader(ComSpacingBehaviour spacing)
    {
        ArgumentNullException.ThrowIfNull(spacing);

        return new ParameterJsonReader(
            new Dictionary<string, GridFactory>(StandardGrids.Factories) { [ComSpacingBehaviour.GridType] = spacing.GridFactory },
            StandardFormatters.All);
    }

    public static PanelSetup Load()
    {
        ComSpacingBehaviour spacing = new();
        ParameterJsonReader reader = CreateReader(spacing);
        var assembly = typeof(DefaultParameters).Assembly;

        List<ParameterFile> files = [.. ResourceNames.Select(name =>
        {
            using Stream json = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing. Is it marked EmbeddedResource in Crowsnest.Core.csproj?");
            return reader.Read(json);
        })];

        ParameterRegistry registry = new(files.SelectMany(f => f.Parameters), files.SelectMany(f => f.Watches));
        spacing.Validate(registry);

        return new PanelSetup(registry, Pages, [spacing]);
    }
}

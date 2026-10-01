using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels.Com;

namespace Crowsnest.Core.Panels;

/// <summary>
/// What the host ships with: every <c>*.parameters.json</c> embedded under <c>Panels/</c>, read
/// with the standard grids and formatters plus the ones panels own, the pages from each
/// module's <c>View/*.view.json</c>, and the panel behaviours.
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
    /// The view files whose pages the host shows, in page order. NAV 1 is registered but has no
    /// view until phase 5 (spec §13).
    /// </summary>
    public static IReadOnlyList<string> ViewResourceNames { get; } =
    [
        "Crowsnest.Core.Panels.Com.View.com.view.json",
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

        Stream Open(string name) => assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing. Is it marked EmbeddedResource in Crowsnest.Core.csproj?");

        List<ParameterFile> files = [.. ResourceNames.Select(name =>
        {
            using Stream json = Open(name);
            return reader.Read(json);
        })];

        List<PanelPage> pages = [.. ViewResourceNames.SelectMany(name =>
        {
            using Stream json = Open(name);
            return PanelViewReader.Read(json);
        })];

        ParameterRegistry registry = new(files.SelectMany(f => f.Parameters), files.SelectMany(f => f.Watches));
        spacing.Validate(registry);

        return new PanelSetup(registry, pages, [spacing]);
    }
}

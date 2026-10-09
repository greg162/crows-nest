using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Application.Panels;

/// <summary>
/// What the host runs: the registry, each panel's pages and any behaviours, built together by
/// <see cref="PanelComposer"/>.
/// </summary>
/// <param name="Panels">Every panel's pages, in catalog order.</param>
/// <param name="DemoValues">
/// What a pretend sim starts with, from the modules' <c>*.demo.json</c> files. Not used against
/// a real sim; there for the fake gateway and the device simulator.
/// </param>
public sealed record PanelSetup(
    ParameterRegistry Registry,
    IReadOnlyList<PanelPages> Panels,
    IReadOnlyList<IPanelBehaviour> Behaviours,
    IReadOnlyDictionary<ParameterId, int> DemoValues)
{
    /// <summary>Every page of every panel, in catalog order.</summary>
    public IReadOnlyList<PanelPage> Pages => [.. Panels.SelectMany(p => p.Pages)];

    /// <summary>Whether a panel by this id ships, as <c>"com"</c> does.</summary>
    public bool HasPanel(string? panelId) => Panels.Any(p => p.PanelId == panelId);

    /// <summary>
    /// The pages of the named panel: what a device assigned it shows (spec §6.2). None for no
    /// panel, or an id that names none.
    /// </summary>
    public IReadOnlyList<PanelPage> PagesFor(string? panelId) =>
        Panels.FirstOrDefault(p => p.PanelId == panelId)?.Pages ?? [];
}

/// <summary>One panel's pages, from its <c>*.view.json</c> files. A panel may have none yet.</summary>
public sealed record PanelPages(string PanelId, IReadOnlyList<PanelPage> Pages);

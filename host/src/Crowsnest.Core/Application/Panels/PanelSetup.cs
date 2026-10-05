using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Application.Panels;

/// <summary>
/// What the host runs: the registry, the pages and any behaviours, built together by
/// <see cref="PanelComposer"/>.
/// </summary>
/// <param name="DemoValues">
/// What a pretend sim starts with, from the modules' <c>*.demo.json</c> files. Not used against
/// a real sim; there for the fake gateway and the device simulator.
/// </param>
public sealed record PanelSetup(
    ParameterRegistry Registry,
    IReadOnlyList<PanelPage> Pages,
    IReadOnlyList<IPanelBehaviour> Behaviours,
    IReadOnlyDictionary<ParameterId, int> DemoValues);

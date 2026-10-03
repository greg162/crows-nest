using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Application.Panels;

/// <summary>
/// Runtime logic for a panel that plain data cannot express (spec §5.8), such as COM's grid
/// following the aircraft's spacing mode. Optional: most panels have none.
///
/// Called on the coordinator's one loop, like everything else in the engine, so it needs no
/// locking. This is the part of the spec's interface that something uses today; <c>OnCommand</c>
/// arrives with the transponder.
/// </summary>
public interface IPanelBehaviour
{
    /// <summary>Sees every sim snapshot, before the parameter's own session does.</summary>
    void OnSnapshot(ParameterSnapshot snapshot, IPanelContext context);
}

/// <summary>What a behaviour may change.</summary>
public interface IPanelContext
{
    ParameterDefinition Parameter(ParameterId id);

    /// <summary>
    /// Swaps a parameter's legal values. A value the pilot is still dialling is dropped: the
    /// sim snaps its own values when a mode changes and reports them like any other change.
    /// </summary>
    void ReplaceGrid(ParameterId id, IValueGrid grid);
}

/// <summary>What the host runs: the registry, the pages and any behaviours, built together.</summary>
public sealed record PanelSetup(ParameterRegistry Registry, IReadOnlyList<PanelPage> Pages, IReadOnlyList<IPanelBehaviour> Behaviours);

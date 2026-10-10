using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Application.Panels;

/// <summary>
/// Runtime logic for a panel that plain data cannot express (spec §5.8), such as COM's grid
/// following the aircraft's spacing mode. Optional: most panels have none.
///
/// Called on the coordinator's one loop, like everything else in the engine, so it needs no
/// locking.
/// </summary>
public interface IPanelBehaviour
{
    /// <summary>Sees every sim snapshot, before the parameter's own session does.</summary>
    void OnSnapshot(ParameterSnapshot snapshot, IPanelContext context);

    /// <summary>
    /// Sees a command the page itself has no use for, such as a tap on a page with no swap
    /// event: the transponder's IDENT. Every behaviour sees it, so check the page is yours.
    /// </summary>
    void OnCommand(BridgeCommand command, PanelPage page, IPanelContext context)
    {
    }

    /// <summary>
    /// Checks the behaviour against the finished registry, so a bad reference in the JSON fails
    /// at startup. Called once, after every module has loaded.
    /// </summary>
    /// <exception cref="InvalidDataException">Something the behaviour depends on is missing.</exception>
    void Validate(ParameterRegistry registry)
    {
    }
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

    /// <summary>Sets the line a page's layout draws under its value; null clears it. Devices showing the page redraw.</summary>
    /// <exception cref="ArgumentException">No page has that id.</exception>
    void SetCaption(string pageId, PageCaption? caption);

    /// <summary>Sends a sim event with no payload. A value still being dialled is not flushed first.</summary>
    void Invoke(string eventName);
}

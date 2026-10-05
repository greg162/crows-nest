namespace Crowsnest.Core.Application.Panels;

/// <summary>
/// One panel family, such as COM or NAV, and everything about it (spec §5.8). A module is a
/// folder under <c>Panels/</c> plus one line in <c>PanelCatalog</c>; nothing else in the code
/// base names it.
///
/// The folder's JSON is found by convention: the module's namespace is its folder
/// (<c>Crowsnest.Core.Panels.Com</c> is <c>Panels/Com/</c>), and every <c>*.parameters.json</c>,
/// <c>*.view.json</c> and <c>*.demo.json</c> in it, or below it, belongs to the module.
/// <see cref="Configure"/> adds only what JSON cannot say: grid types, formatters and
/// behaviours. A panel that is pure data configures nothing.
/// </summary>
public interface IPanelModule
{
    /// <summary>
    /// Lower case, such as <c>"com"</c>. Every parameter, watch and page id in the module starts
    /// with it (<c>com1.standby</c>, <c>com2</c>), so two modules cannot collide.
    /// </summary>
    string Id { get; }

    /// <summary>Called once per load, so a behaviour created here belongs to that load alone.</summary>
    void Configure(PanelBuilder panel);
}

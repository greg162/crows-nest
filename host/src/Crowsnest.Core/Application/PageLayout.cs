namespace Crowsnest.Core.Application;

/// <summary>
/// The closed set of layouts the firmware implements literally (spec §5.5).
/// The host picks one and fills it; the device never learns what a field means.
/// </summary>
public enum PageLayout
{
    ActiveStandbyPair,
    SingleValue,
    DualValue,

    /// <summary>One value, large and green, in the P180 frame with the title in its border (firmware 0.4.0).</summary>
    FramedValue,
}

/// <summary>
/// The names of the layouts, as the wire, the firmware's caps and the view files all spell
/// them (spec §5.5, §6.1). One list, so the three cannot drift apart. Case-sensitive, like
/// the protocol.
/// </summary>
public static class PageLayoutNames
{
    private static readonly Dictionary<string, PageLayout> ByName = new(StringComparer.Ordinal)
    {
        ["pair"] = PageLayout.ActiveStandbyPair,
        ["single"] = PageLayout.SingleValue,
        ["dual"] = PageLayout.DualValue,
        ["framed"] = PageLayout.FramedValue,
    };

    public static IReadOnlyCollection<string> All => ByName.Keys;

    public static string Of(PageLayout layout) =>
        ByName.FirstOrDefault(p => p.Value == layout).Key
        ?? throw new ArgumentOutOfRangeException(nameof(layout));

    /// <summary>Null for a name this host does not know, such as a newer firmware's layout.</summary>
    public static PageLayout? Parse(string? name) =>
        name is not null && ByName.TryGetValue(name, out PageLayout layout) ? layout : null;
}

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
}

namespace Crowsnest.Core.Domain;

/// <summary>
/// Names one tunable parameter: <c>"com1.standby"</c>, <c>"nav1.active"</c>, <c>"ap.altitude"</c>
/// (spec §5.0). A string rather than an enum, because parameters are defined in data and adding
/// NAV 2 must not mean adding an enum member.
/// </summary>
public readonly record struct ParameterId
{
    public ParameterId(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
    }

    public string Key { get; }

    public override string ToString() => Key;
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Crowsnest.Core.Application.Settings;

/// <summary>
/// What the user has set (spec §6.2, §8): for now, which panel each device shows and how
/// bright it is. Kept in <c>settings.json</c>, shaped like
///
/// <code>
/// { "devices": {
///     "c81234":       { "name": "Radios", "panel": "com", "brightness": 80 },
///     "a4cf12de9f44": { "panel": "nav" } } }
/// </code>
///
/// A device is named by its hardware id: all twelve characters, or the last six, which is what
/// its screen shows. One device, one panel: a COM unit, a NAV unit. Comments and trailing
/// commas are allowed; an unknown key is an error, so a typo such as <c>"pannel"</c> is
/// reported rather than leaving the device unassigned.
/// </summary>
public sealed partial record BridgeSettings(IReadOnlyDictionary<string, DeviceSettings> Devices)
{
    public static BridgeSettings Empty { get; } = new(new Dictionary<string, DeviceSettings>());

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// The settings for a device, by its full hardware id or, failing that, its last six
    /// characters. Null if neither is listed.
    /// </summary>
    public DeviceSettings? Find(string hardwareId) => KeyFor(hardwareId) is { } key ? Devices[key] : null;

    /// <summary>
    /// Which entry <see cref="Find"/> uses for a device: its full hardware id or, failing that,
    /// its last six characters. Null if neither is listed.
    /// </summary>
    public string? KeyFor(string hardwareId)
    {
        ArgumentException.ThrowIfNullOrEmpty(hardwareId);

        string id = hardwareId.ToLowerInvariant();
        if (Devices.ContainsKey(id))
        {
            return id;
        }

        return id.Length > 6 && Devices.ContainsKey(id[^6..]) ? id[^6..] : null;
    }

    /// <exception cref="InvalidDataException">The file is malformed or a value is out of range, with the device named.</exception>
    public static BridgeSettings Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SettingsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SettingsDto>(json, Options);
        }
        catch (JsonException e)
        {
            // Name the place in the file, not the class it failed to fill.
            string where = string.IsNullOrEmpty(e.Path) ? "" : $" at {e.Path}";
            string why = e.Message.Contains("could not be mapped", StringComparison.Ordinal) ? "that is not a setting Crowsnest knows" : e.Message;
            throw new InvalidDataException($"Settings are malformed{where}: {why}", e);
        }

        Dictionary<string, DeviceSettings> devices = [];
        foreach ((string key, DeviceDto? device) in dto?.Devices ?? [])
        {
            InvalidDataException Invalid(string problem) => new($"Settings for device '{key}': {problem}.");

            string id = key.ToLowerInvariant();
            if (!DeviceKey().IsMatch(id))
            {
                throw Invalid("a device is named by its hardware id, all 12 hex characters or the last 6 its screen shows");
            }

            if (device?.Brightness is < 0 or > 100)
            {
                throw Invalid($"\"brightness\" is {device.Brightness}; it goes from 0 to 100");
            }

            string? panel = device?.Panel;
            if (panel is not null && string.IsNullOrWhiteSpace(panel))
            {
                throw Invalid("\"panel\" is empty; leave it out for a device that shows nothing");
            }

            if (!devices.TryAdd(id, new DeviceSettings(device?.Name, panel, device?.Brightness)))
            {
                throw Invalid("it is listed twice");
            }
        }

        return new BridgeSettings(devices);
    }

    public void Write(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonSerializer.Serialize(
            json,
            new SettingsDto(Devices.ToDictionary(d => d.Key, d => (DeviceDto?)new DeviceDto(d.Value.Name, d.Value.Panel, d.Value.Brightness))),
            Options);
    }

    [GeneratedRegex("^([0-9a-f]{6}|[0-9a-f]{12})$")]
    private static partial Regex DeviceKey();

    private sealed record SettingsDto(Dictionary<string, DeviceDto?>? Devices);

    private sealed record DeviceDto(string? Name, string? Panel, int? Brightness);
}

/// <param name="Name">For the user: "Radios". Shown in the log.</param>
/// <param name="Panel">The panel it shows, such as <c>"com"</c>; it pages through that panel's pages. Null: unassigned.</param>
/// <param name="Brightness">0 to 100; null for the default.</param>
public sealed record DeviceSettings(string? Name, string? Panel, int? Brightness);

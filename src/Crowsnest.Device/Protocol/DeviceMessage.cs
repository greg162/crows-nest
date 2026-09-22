using System.Text.Json.Serialization;

namespace Crowsnest.Device.Protocol;

/// <summary>Device → host frames (spec §6.1).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(DeviceHello), "hello")]
[JsonDerivedType(typeof(DeviceInputFrame), "input")]
[JsonDerivedType(typeof(DevicePong), "pong")]
[JsonDerivedType(typeof(DeviceLog), "log")]
public abstract record DeviceMessage
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolVersion.Current;
}

public sealed record DeviceHello : DeviceMessage
{
    /// <summary>"crowpanel-2.1-rotary".</summary>
    [JsonPropertyName("dev")]
    public required string DeviceType { get; init; }

    [JsonPropertyName("fw")]
    public required string FirmwareVersion { get; init; }

    /// <summary>eFuse MAC, lower case, no separators. The only stable panel identity (spec §6.2).</summary>
    [JsonPropertyName("id")]
    public required string HardwareId { get; init; }

    [JsonPropertyName("caps")]
    public required WireCapabilities Capabilities { get; init; }
}

public sealed record WireCapabilities
{
    /// <summary>"round" | "rect".</summary>
    [JsonPropertyName("shape")]
    public string Shape { get; init; } = "rect";

    [JsonPropertyName("w")]
    public int Width { get; init; }

    [JsonPropertyName("h")]
    public int Height { get; init; }

    [JsonPropertyName("encoder")]
    public bool Encoder { get; init; }

    [JsonPropertyName("detentsPerClick")]
    public int DetentsPerClick { get; init; } = 1;

    [JsonPropertyName("touch")]
    public bool Touch { get; init; }

    [JsonPropertyName("buttons")]
    public int Buttons { get; init; }

    [JsonPropertyName("maxFields")]
    public int MaxFields { get; init; }

    [JsonPropertyName("layouts")]
    public IReadOnlyList<string> Layouts { get; init; } = [];
}

public sealed record DeviceInputFrame : DeviceMessage
{
    [JsonPropertyName("seq")]
    public long Sequence { get; init; }

    /// <summary>"encoder" | "press" | "tap" | "swipe".</summary>
    [JsonPropertyName("ev")]
    public required string Event { get; init; }

    /// <summary>Signed detent count, for "encoder".</summary>
    [JsonPropertyName("d")]
    public int Detents { get; init; }

    /// <summary>"short" | "long", for "press".</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>"left" | "right" | "up" | "down", for "swipe".</summary>
    [JsonPropertyName("dir")]
    public string? Direction { get; init; }
}

public sealed record DevicePong : DeviceMessage
{
    [JsonPropertyName("ts")]
    public long Timestamp { get; init; }
}

public sealed record DeviceLog : DeviceMessage
{
    [JsonPropertyName("lvl")]
    public string Level { get; init; } = "info";

    [JsonPropertyName("msg")]
    public string Message { get; init; } = string.Empty;
}

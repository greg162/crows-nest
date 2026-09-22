using System.Text.Json.Serialization;

namespace Crowsnest.Device.Protocol;

/// <summary>
/// Host → device frames (spec §6.1). One JSON object per line, UTF-8, "t" discriminates.
/// Unknown fields are ignored on both ends so the two sides version independently.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HostHello), "hello")]
[JsonDerivedType(typeof(HostHelloAck), "hello_ack")]
[JsonDerivedType(typeof(HostState), "state")]
[JsonDerivedType(typeof(HostNotice), "notice")]
[JsonDerivedType(typeof(HostPing), "ping")]
public abstract record HostMessage
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolVersion.Current;
}

public sealed record HostHello : HostMessage
{
    [JsonPropertyName("host")]
    public required string Host { get; init; }
}

public sealed record HostHelloAck : HostMessage
{
    [JsonPropertyName("proto")]
    public int Proto { get; init; } = ProtocolVersion.Current;

    [JsonPropertyName("cfg")]
    public HostConfig? Config { get; init; }
}

public sealed record HostConfig
{
    [JsonPropertyName("brightness")]
    public int Brightness { get; init; } = 80;

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "day";
}

public sealed record HostState : HostMessage
{
    [JsonPropertyName("rev")]
    public long Revision { get; init; }

    [JsonPropertyName("ack")]
    public long Ack { get; init; }

    [JsonPropertyName("sim")]
    public string Sim { get; init; } = "disconnected";

    [JsonPropertyName("page")]
    public required WirePage Page { get; init; }

    [JsonPropertyName("fields")]
    public required IReadOnlyList<WireField> Fields { get; init; }
}

public sealed record WirePage
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>"pair" | "single" | "dual".</summary>
    [JsonPropertyName("layout")]
    public required string Layout { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed record WireField
{
    /// <summary>"primary" | "secondary" | "tertiary".</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>[start, end) character range into Text. Omitted when there is no cursor.</summary>
    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? Cursor { get; init; }

    [JsonPropertyName("pending")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Pending { get; init; }
}

public sealed record HostNotice : HostMessage
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

public sealed record HostPing : HostMessage
{
    [JsonPropertyName("ts")]
    public long Timestamp { get; init; }
}

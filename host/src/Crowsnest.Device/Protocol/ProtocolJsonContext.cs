using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crowsnest.Device.Protocol;

/// <summary>
/// Source-generated serialisation for the wire protocol. Reflection-based serialisation
/// is unavailable once Crowsnest is published trimmed and self-contained (spec §12).
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    // The spec writes "v" ahead of "t" (§6.1) and the firmware emits frames that way, but
    // System.Text.Json wants the type discriminator first and throws otherwise. Without
    // this, every frame from a real panel is silently discarded as malformed.
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(DeviceMessage))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;

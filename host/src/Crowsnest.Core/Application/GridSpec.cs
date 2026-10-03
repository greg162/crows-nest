using System.Text.Json;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Grids;

namespace Crowsnest.Core.Application;

/// <summary>
/// The <c>"grid"</c> object of one registry entry, handed to the factory for its <c>"type"</c>
/// (spec §5.2). Panels that own a grid type read their own fields from it, so the registry
/// never has to know about <c>comChannel</c> or <c>digits</c>.
/// </summary>
public sealed class GridSpec
{
    private readonly JsonElement _json;

    internal GridSpec(ParameterId parameter, string type, JsonElement json)
    {
        Parameter = parameter;
        Type = type;
        _json = json;
    }

    /// <summary>The entry this grid belongs to, for error messages.</summary>
    public ParameterId Parameter { get; }

    public string Type { get; }

    public int RequireInt(string name) =>
        OptionalInt(name) ?? throw Invalid($"needs an integer \"{name}\"");

    public int? OptionalInt(string name)
    {
        if (!_json.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : throw Invalid($"\"{name}\" must be an integer");
    }

    public string? OptionalString(string name)
    {
        if (!_json.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw Invalid($"\"{name}\" must be a string");
    }

    public InvalidDataException Invalid(string problem) =>
        new($"Parameter '{Parameter}': the {Type} grid {problem}.");
}

/// <summary>Builds a grid from its spec. Throws <see cref="InvalidDataException"/> (via <see cref="GridSpec.Invalid"/>) or <see cref="ArgumentException"/> on bad values.</summary>
public delegate IValueGrid GridFactory(GridSpec spec);

/// <summary>The grid types in <c>Domain/Grids/</c>, which any panel may use.</summary>
public static class StandardGrids
{
    public static IReadOnlyDictionary<string, GridFactory> Factories { get; } = new Dictionary<string, GridFactory>
    {
        ["linear"] = s => new LinearGrid(s.RequireInt("min"), s.RequireInt("max"), s.RequireInt("step"), s.OptionalInt("parent")),
        ["wrapping"] = s => new WrappingGrid(s.RequireInt("min"), s.RequireInt("max"), s.RequireInt("step")),
        ["signedLinear"] = s => new SignedLinearGrid(s.RequireInt("min"), s.RequireInt("max"), s.RequireInt("step")),
    };
}

using System.Text.Json;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Formatting;

namespace Crowsnest.Core.Application;

/// <summary>
/// Reads registry entries from JSON (spec §5.2): an array of objects shaped like
///
/// <code>
/// { "id": "nav1.standby", "label": "NAV 1 STBY", "group": "nav1", "unit": "kHz",
///   "grid":    { "type": "linear", "min": 108000, "max": 117950, "step": 50, "parent": 1000 },
///   "cursors": [ { "name": "mhz", "step": 1000, "wrap": "clamp", "span": "0..3" }, ... ],
///   "read":    { "source": "simvar", "name": "NAV STANDBY FREQUENCY:1", "unit": "Hz", "scale": 0.001 },
///   "write":   { "mode": "keyEvent", "target": "NAV1_STBY_SET_HZ", "encoding": "hz" },
///   "format":  "freq2" }
/// </code>
///
/// An entry with <c>"kind": "watch"</c> is a value read from the sim but never tuned, for a
/// panel behaviour to follow (<c>com1.spacing</c>); it has only <c>id</c> and <c>read</c>.
///
/// Every field of a parameter is required except <c>cursors[].wrap</c> (default <c>carry</c>, which every grid
/// accepts) and <c>read.scale</c> (default 1). Comments and trailing commas are allowed. Enum
/// values are case-insensitive. Anything wrong throws <see cref="InvalidDataException"/> naming
/// the entry and the field, so a bad entry fails at startup and in the registry test rather than
/// on the first turn of a knob.
/// </summary>
public sealed class ParameterJsonReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Dictionary<string, CanonicalUnit> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kHz"] = CanonicalUnit.Kilohertz,
        ["ft"] = CanonicalUnit.Feet,
        ["deg"] = CanonicalUnit.Degrees,
        ["fpm"] = CanonicalUnit.FeetPerMinute,
        ["kt"] = CanonicalUnit.Knots,
        ["mb"] = CanonicalUnit.Millibars,
        ["code"] = CanonicalUnit.OctalCode,
    };

    private readonly IReadOnlyDictionary<string, GridFactory> _grids;
    private readonly IReadOnlyDictionary<string, IValueFormatter> _formatters;

    public ParameterJsonReader(IReadOnlyDictionary<string, GridFactory> grids, IReadOnlyDictionary<string, IValueFormatter> formatters)
    {
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(formatters);

        _grids = grids;
        _formatters = formatters;
    }

    public ParameterFile Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        EntryDto[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<EntryDto[]>(json, Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Parameter JSON is malformed: {e.Message}", e);
        }

        if (entries is null)
        {
            throw new InvalidDataException("Parameter JSON must be an array of entries, not null.");
        }

        List<ParameterDefinition> parameters = [];
        List<SimSubscription> watches = [];

        for (int position = 0; position < entries.Length; position++)
        {
            EntryDto entry = entries[position];
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                throw new InvalidDataException($"Parameter entry {position} has no \"id\".");
            }

            ParameterId id = new(entry.Id);
            switch (entry.Kind?.ToUpperInvariant())
            {
                case null or "PARAMETER":
                    parameters.Add(Build(id, entry));
                    break;

                case "WATCH":
                    watches.Add(BuildWatch(id, entry));
                    break;

                default:
                    throw new InvalidDataException($"Parameter '{id}': \"kind\" is \"{entry.Kind}\", which is not parameter or watch.");
            }
        }

        return new ParameterFile(parameters, watches);
    }

    private static SimSubscription BuildWatch(ParameterId id, EntryDto entry)
    {
        if (entry.Grid is not null || entry.Cursors is not null || entry.Write is not null || entry.Format is not null)
        {
            throw new InvalidDataException($"Watch '{id}': a watch is only read, so it has no grid, cursors, write or format.");
        }

        return new SimSubscription(id, BuildRead(entry.Read, problem => new InvalidDataException($"Watch '{id}': {problem}.")));
    }

    private static ReadBinding BuildRead(ReadDto? dto, Func<string, InvalidDataException> invalid)
    {
        ReadDto read = dto ?? throw invalid("\"read\" is required");

        string Require(string? value, string field) =>
            string.IsNullOrWhiteSpace(value) ? throw invalid($"\"{field}\" is required") : value;

        ReadSource source = Enum.TryParse(Require(read.Source, "read.source"), ignoreCase: true, out ReadSource s) && Enum.IsDefined(s)
            ? s
            : throw invalid($"\"read.source\" is \"{read.Source}\", which is not one of {string.Join(", ", Enum.GetNames<ReadSource>())}");

        return new ReadBinding(
            source,
            Require(read.Name, "read.name"),
            Require(read.Unit, "read.unit"),
            read.Scale is null or > 0 ? read.Scale ?? 1 : throw invalid("\"read.scale\" must be positive"));
    }

    private ParameterDefinition Build(ParameterId id, EntryDto entry)
    {
        InvalidDataException Invalid(string problem) => new($"Parameter '{id}': {problem}.");

        string Require(string? value, string field) =>
            string.IsNullOrWhiteSpace(value) ? throw Invalid($"\"{field}\" is required") : value;

        T Parse<T>(string? value, string field)
            where T : struct, Enum =>
            Enum.TryParse(Require(value, field), ignoreCase: true, out T parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw Invalid($"\"{field}\" is \"{value}\", which is not one of {string.Join(", ", Enum.GetNames<T>())}");

        CanonicalUnit unit = Units.TryGetValue(Require(entry.Unit, "unit"), out CanonicalUnit u)
            ? u
            : throw Invalid($"\"unit\" is \"{entry.Unit}\", which is not one of {string.Join(", ", Units.Keys)}");

        // Grid.
        if (entry.Grid is not { ValueKind: JsonValueKind.Object } gridJson)
        {
            throw Invalid("\"grid\" must be an object");
        }

        string gridType = gridJson.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : throw Invalid("\"grid.type\" is required");

        GridFactory factory = _grids.TryGetValue(gridType, out GridFactory? f)
            ? f
            : throw Invalid($"no grid type \"{gridType}\" is registered");

        Domain.Grids.IValueGrid grid;
        try
        {
            grid = factory(new GridSpec(id, gridType, gridJson));
        }
        catch (ArgumentException e)
        {
            throw Invalid($"the {gridType} grid is invalid: {e.Message}");
        }

        // Cursors. Each is tried against the grid once, so a step or wrap mode the grid rejects
        // fails here rather than on the first detent.
        if (entry.Cursors is not { Length: > 0 })
        {
            throw Invalid("\"cursors\" must list at least one cursor");
        }

        List<CursorLevel> cursors = [];
        foreach (CursorDto c in entry.Cursors)
        {
            string name = Require(c.Name, "cursors[].name");
            int step = c.Step ?? throw Invalid($"cursor '{name}' needs a \"step\"");
            CursorWrap wrap = c.Wrap is null ? CursorWrap.Carry : Parse<CursorWrap>(c.Wrap, $"cursor '{name}' wrap");
            Range span = CursorSpans.TryParse(c.Span, out Range s)
                ? s
                : throw Invalid($"cursor '{name}' needs a \"span\" like \"4..7\" or \"..^4\", not \"{c.Span}\"");

            CursorLevel cursor = new(name, step, wrap, span);
            try
            {
                grid.Step(grid.Snap(0), 1, cursor);
            }
            catch (ArgumentException e)
            {
                throw Invalid($"cursor '{name}' does not fit the {gridType} grid: {e.Message}");
            }

            cursors.Add(cursor);
        }

        if (cursors.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != cursors.Count)
        {
            throw Invalid("two cursors share a name");
        }

        // Bindings.
        ReadBinding read = BuildRead(entry.Read, Invalid);

        WriteDto writeDto = entry.Write ?? throw Invalid("\"write\" is required");
        WriteBinding write = new(
            Parse<WriteMode>(writeDto.Mode, "write.mode"),
            Require(writeDto.Target, "write.target"),
            Parse<PayloadEncoding>(writeDto.Encoding, "write.encoding"));

        IValueFormatter formatter = _formatters.TryGetValue(Require(entry.Format, "format"), out IValueFormatter? fm)
            ? fm
            : throw Invalid($"no formatter \"{entry.Format}\" is registered");

        return new ParameterDefinition(id, Require(entry.Label, "label"), Require(entry.Group, "group"), unit, grid, cursors, read, write, formatter);
    }

    // Deserialisation targets. Everything nullable so a missing field becomes a named error
    // rather than a default value.
    private sealed record EntryDto(
        string? Kind, string? Id, string? Label, string? Group, string? Unit, JsonElement? Grid,
        CursorDto[]? Cursors, ReadDto? Read, WriteDto? Write, string? Format);

    private sealed record CursorDto(string? Name, int? Step, string? Wrap, string? Span);

    private sealed record ReadDto(string? Source, string? Name, string? Unit, double? Scale);

    private sealed record WriteDto(string? Mode, string? Target, string? Encoding);
}

/// <summary>What one JSON file defines: tunable parameters and watched values.</summary>
public sealed record ParameterFile(IReadOnlyList<ParameterDefinition> Parameters, IReadOnlyList<SimSubscription> Watches);

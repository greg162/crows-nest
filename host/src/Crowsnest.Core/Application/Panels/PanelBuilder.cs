using Crowsnest.Core.Domain.Formatting;

namespace Crowsnest.Core.Application.Panels;

/// <summary>
/// What a module adds beyond its JSON (spec §5.8). Grid types and formatters are the module's
/// own: its JSON may name them, other modules' JSON may not. The standard ones
/// (<see cref="StandardGrids"/>, <see cref="StandardFormatters"/>) are always available.
/// </summary>
public sealed class PanelBuilder
{
    private readonly Dictionary<string, GridFactory> _grids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IValueFormatter> _formatters = new(StringComparer.Ordinal);
    private readonly List<IPanelBehaviour> _behaviours = [];

    internal PanelBuilder(string panelId) => PanelId = panelId;

    internal string PanelId { get; }

    internal IReadOnlyDictionary<string, GridFactory> Grids => _grids;

    internal IReadOnlyDictionary<string, IValueFormatter> Formatters => _formatters;

    internal IReadOnlyList<IPanelBehaviour> Behaviours => _behaviours;

    /// <summary>A grid type this module's JSON names in <c>"grid": { "type": ... }</c>.</summary>
    public PanelBuilder AddGridType(string type, GridFactory factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(factory);

        if (StandardGrids.Factories.ContainsKey(type) || !_grids.TryAdd(type, factory))
        {
            throw new InvalidOperationException($"Panel '{PanelId}' adds grid type '{type}' twice, or one that is already standard.");
        }

        return this;
    }

    /// <summary>A formatter this module's JSON names in <c>"format"</c>.</summary>
    public PanelBuilder AddFormatter(string key, IValueFormatter formatter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(formatter);

        if (StandardFormatters.All.ContainsKey(key) || !_formatters.TryAdd(key, formatter))
        {
            throw new InvalidOperationException($"Panel '{PanelId}' adds formatter '{key}' twice, or one that is already standard.");
        }

        return this;
    }

    /// <summary>Runtime logic the JSON cannot express. It sees every sim snapshot, not only its module's.</summary>
    public PanelBuilder AddBehaviour(IPanelBehaviour behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);

        _behaviours.Add(behaviour);
        return this;
    }
}

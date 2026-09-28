using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Application;

/// <summary>
/// Every parameter the host knows, and every watched value, by id (spec §5.2). Immutable once
/// built. Ids are unique across both.
///
/// The spec's <c>Groups</c> (active/standby pairs with their swap event) are not here yet:
/// swap events belong to pages, which the panel modules (§5.8) contribute.
/// </summary>
public sealed class ParameterRegistry
{
    private readonly Dictionary<ParameterId, ParameterDefinition> _byId = [];

    public ParameterRegistry(IEnumerable<ParameterDefinition> parameters, IEnumerable<SimSubscription>? watches = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        All = [.. parameters];
        Watches = [.. watches ?? []];

        HashSet<ParameterId> ids = [];
        foreach (ParameterId id in All.Select(p => p.Id).Concat(Watches.Select(w => w.Id)))
        {
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"'{id}' is defined twice.");
            }
        }

        foreach (ParameterDefinition parameter in All)
        {
            _byId.Add(parameter.Id, parameter);
        }

        Subscriptions = [.. All.Select(p => new SimSubscription(p.Id, p.Read)), .. Watches];
    }

    /// <summary>In the order they were defined.</summary>
    public IReadOnlyList<ParameterDefinition> All { get; }

    /// <summary>Values read for panel behaviours but never tuned, such as <c>com1.spacing</c>.</summary>
    public IReadOnlyList<SimSubscription> Watches { get; }

    /// <summary>Everything the gateway subscribes to: every parameter, then every watch (spec §7.3).</summary>
    public IReadOnlyList<SimSubscription> Subscriptions { get; }

    /// <exception cref="KeyNotFoundException">No parameter has this id.</exception>
    public ParameterDefinition this[ParameterId id] =>
        _byId.TryGetValue(id, out ParameterDefinition? parameter)
            ? parameter
            : throw new KeyNotFoundException($"No parameter '{id}' is registered.");

    public bool TryGet(ParameterId id, out ParameterDefinition parameter) =>
        _byId.TryGetValue(id, out parameter!);
}

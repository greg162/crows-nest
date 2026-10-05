using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Domain.Formatting;

namespace Crowsnest.Core.Application.Panels;

/// <summary>One JSON file a module brings: its name within the module's folder, and how to read it.</summary>
public sealed record PanelFile(string Name, Func<Stream> Open);

/// <summary>
/// Builds the <see cref="PanelSetup"/> from a list of modules (spec §5.8): configures each
/// module, reads its JSON with the standard grids and formatters plus its own, and checks that
/// the module keeps to itself. Pages come in module order, then file order.
///
/// Anything wrong throws <see cref="InvalidDataException"/> naming the panel and the file, so a
/// mistake in a new panel fails at startup and in the catalog tests, never on a knob turn.
/// </summary>
public static partial class PanelComposer
{
    private static readonly JsonSerializerOptions DemoOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads each module's JSON from the embedded resources under its namespace.</summary>
    public static PanelSetup Compose(IReadOnlyList<IPanelModule> modules) => Compose(modules, EmbeddedFiles);

    /// <summary>Reads each module's JSON from <paramref name="filesOf"/> instead, for tests.</summary>
    public static PanelSetup Compose(IReadOnlyList<IPanelModule> modules, Func<IPanelModule, IReadOnlyList<PanelFile>> filesOf)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(filesOf);

        List<ParameterDefinition> parameters = [];
        List<SimSubscription> watches = [];
        List<PanelPage> pages = [];
        List<IPanelBehaviour> behaviours = [];
        Dictionary<ParameterId, int> demoValues = [];
        HashSet<string> ids = new(StringComparer.Ordinal);

        foreach (IPanelModule module in modules)
        {
            string id = module.Id;
            if (id is null || !ModuleId().IsMatch(id))
            {
                throw new InvalidDataException($"{module.GetType().Name}: a panel id is lower-case letters and digits, starting with a letter, not '{id}'.");
            }

            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Two panels have the id '{id}'.");
            }

            PanelBuilder builder = new(id);
            module.Configure(builder);

            ModuleContents contents = Read(builder, filesOf(module));
            contents.CheckOwnership(id);

            parameters.AddRange(contents.Parameters);
            watches.AddRange(contents.Watches);
            pages.AddRange(contents.Pages);
            behaviours.AddRange(builder.Behaviours);
            foreach ((ParameterId parameter, int value) in contents.DemoValues)
            {
                demoValues.Add(parameter, value);
            }
        }

        ParameterRegistry registry = new(parameters, watches);
        foreach (IPanelBehaviour behaviour in behaviours)
        {
            behaviour.Validate(registry);
        }

        return new PanelSetup(registry, pages, behaviours, demoValues);
    }

    /// <summary>
    /// The embedded resources in the module's folder: those whose names start with its namespace.
    /// Resource names follow the folder, so <c>Panels/Com/View/com.view.json</c> is
    /// <c>Crowsnest.Core.Panels.Com.View.com.view.json</c> and its name here is <c>View.com.view.json</c>.
    /// </summary>
    public static IReadOnlyList<PanelFile> EmbeddedFiles(IPanelModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        Type type = module.GetType();
        string prefix = (type.Namespace ?? throw new InvalidDataException($"{type.Name} has no namespace, so it has no folder.")) + ".";
        Assembly assembly = type.Assembly;

        return [.. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => new PanelFile(
                name[prefix.Length..],
                () => assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource '{name}' vanished.")))];
    }

    private static ModuleContents Read(PanelBuilder builder, IReadOnlyList<PanelFile> files)
    {
        ParameterJsonReader reader = new(
            Merge(StandardGrids.Factories, builder.Grids),
            Merge(StandardFormatters.All, builder.Formatters));

        ModuleContents contents = new();
        foreach (PanelFile file in files)
        {
            try
            {
                using Stream json = file.Open();
                if (file.Name.EndsWith(".parameters.json", StringComparison.Ordinal))
                {
                    ParameterFile read = reader.Read(json);
                    contents.Parameters.AddRange(read.Parameters);
                    contents.Watches.AddRange(read.Watches);
                }
                else if (file.Name.EndsWith(".view.json", StringComparison.Ordinal))
                {
                    contents.Pages.AddRange(PanelViewReader.Read(json));
                }
                else if (file.Name.EndsWith(".demo.json", StringComparison.Ordinal))
                {
                    contents.DemoFiles.Add((file.Name, ReadDemo(json)));
                }
                else
                {
                    throw new InvalidDataException("a panel's files are *.parameters.json, *.view.json or *.demo.json");
                }
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"Panel '{builder.PanelId}', {file.Name}: {e.Message}", e);
            }
        }

        if (contents.Parameters.Count == 0 && contents.Pages.Count == 0)
        {
            throw new InvalidDataException(
                $"Panel '{builder.PanelId}' found no *.parameters.json or *.view.json. Its namespace must match its folder under Panels/, " +
                "and the files must be embedded resources.");
        }

        contents.ResolveDemoValues(builder.PanelId);
        return contents;
    }

    private static Dictionary<string, int> ReadDemo(Stream json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json, DemoOptions)
                ?? throw new InvalidDataException("a demo file is an object of ids and values, not null");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Demo JSON must be an object of ids and integer values: {e.Message}", e);
        }
    }

    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> standard, IReadOnlyDictionary<string, T> own)
    {
        Dictionary<string, T> merged = new(standard, StringComparer.Ordinal);
        foreach ((string key, T value) in own)
        {
            merged.Add(key, value);
        }

        return merged;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex ModuleId();

    private sealed class ModuleContents
    {
        public List<ParameterDefinition> Parameters { get; } = [];

        public List<SimSubscription> Watches { get; } = [];

        public List<PanelPage> Pages { get; } = [];

        public List<(string File, Dictionary<string, int> Values)> DemoFiles { get; } = [];

        public Dictionary<ParameterId, int> DemoValues { get; } = [];

        public void ResolveDemoValues(string panelId)
        {
            HashSet<ParameterId> known = [.. Parameters.Select(p => p.Id), .. Watches.Select(w => w.Id)];
            foreach ((string file, Dictionary<string, int> values) in DemoFiles)
            {
                foreach ((string key, int value) in values)
                {
                    ParameterId id = new(key);
                    if (!known.Contains(id))
                    {
                        throw new InvalidDataException($"Panel '{panelId}', {file}: '{id}' is not one of this panel's parameters or watches.");
                    }

                    if (!DemoValues.TryAdd(id, value))
                    {
                        throw new InvalidDataException($"Panel '{panelId}', {file}: '{id}' is given a demo value twice.");
                    }
                }
            }
        }

        /// <summary>Every id starts with the panel's, and pages show only the panel's own parameters.</summary>
        public void CheckOwnership(string panelId)
        {
            InvalidDataException Invalid(string problem) => new($"Panel '{panelId}': {problem}.");

            foreach (ParameterId id in Parameters.Select(p => p.Id).Concat(Watches.Select(w => w.Id)))
            {
                if (!id.Key.StartsWith(panelId, StringComparison.Ordinal))
                {
                    throw Invalid($"'{id}' must start with '{panelId}', so it cannot clash with another panel's");
                }
            }

            HashSet<ParameterId> own = [.. Parameters.Select(p => p.Id)];
            foreach (PanelPage page in Pages)
            {
                if (!page.Id.StartsWith(panelId, StringComparison.Ordinal))
                {
                    throw Invalid($"page '{page.Id}' must start with '{panelId}'");
                }

                if (page.Fields.FirstOrDefault(f => !own.Contains(f)) is { Key: not null } stranger)
                {
                    throw Invalid($"page '{page.Id}' shows '{stranger}', which is not one of this panel's parameters");
                }

                if (page.SwapEvent is not null && page.Fields.Count != 2)
                {
                    throw Invalid($"page '{page.Id}' has a swap event, so it needs exactly two fields to swap, not {page.Fields.Count}");
                }
            }
        }
    }
}

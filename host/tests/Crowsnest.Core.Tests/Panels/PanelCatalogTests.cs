using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Panels;

namespace Crowsnest.Core.Tests.Panels;

/// <summary>
/// Every shipped panel, checked the same way (spec §5.8). A new module gets these for free: it
/// only has to be in <see cref="PanelCatalog"/>, and the first two tests say so if it is not.
/// </summary>
public class PanelCatalogTests
{
    private const string PanelsNamespace = "Crowsnest.Core.Panels.";

    public static TheoryData<string> ModuleIds => [.. PanelCatalog.All.Select(m => m.Id)];

    [Fact]
    public void EveryModuleIsInTheCatalog()
    {
        IEnumerable<Type> registered = PanelCatalog.All.Select(m => m.GetType());

        Type[] missing = [.. typeof(PanelCatalog).Assembly.GetTypes()
            .Where(t => typeof(IPanelModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Except(registered)];

        Assert.True(missing.Length == 0, $"Add {string.Join(", ", missing.Select(t => $"new {t.Name}()"))} to PanelCatalog.All in Panels/PanelCatalog.cs.");
    }

    [Fact]
    public void EveryPanelFileBelongsToAModuleInTheCatalog()
    {
        string[] owners = [.. PanelCatalog.All.Select(m => m.GetType().Namespace + ".")];

        string[] orphans = [.. typeof(PanelCatalog).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(PanelsNamespace, StringComparison.Ordinal))
            .Where(name => !owners.Any(owner => name.StartsWith(owner, StringComparison.Ordinal)))];

        Assert.True(orphans.Length == 0,
            $"No module in PanelCatalog loads {string.Join(", ", orphans)}. Each folder under Panels/ needs a module " +
            "in the folder's namespace (see Panels/README.md), listed in PanelCatalog.All.");
    }

    [Fact]
    public void TheCatalogLoads()
    {
        PanelSetup setup = PanelCatalog.Load();

        Assert.NotEmpty(setup.Pages);
    }

    [Fact]
    public void EachLoadHasItsOwnBehaviours() =>
        Assert.Empty(PanelCatalog.Load().Behaviours.Intersect(PanelCatalog.Load().Behaviours));

    /// <summary>A module stands alone: it does not lean on another module's parameters, grids or formatters.</summary>
    [Theory]
    [MemberData(nameof(ModuleIds))]
    public void EachModuleLoadsOnItsOwn(string id)
    {
        IPanelModule module = PanelCatalog.All.Single(m => m.Id == id);

        PanelSetup setup = PanelComposer.Compose([module]);

        Assert.All(setup.Registry.Subscriptions, s => Assert.StartsWith(id, s.Id.Key, StringComparison.Ordinal));
    }

    /// <summary>Demo values are where the fake sim starts, so they should be legal values.</summary>
    [Theory]
    [MemberData(nameof(ModuleIds))]
    public void EachModulesDemoValuesAreOnTheGrid(string id)
    {
        PanelSetup setup = PanelComposer.Compose([PanelCatalog.All.Single(m => m.Id == id)]);

        foreach (var (parameter, value) in setup.DemoValues)
        {
            if (setup.Registry.TryGet(parameter, out var definition))
            {
                Assert.True(definition.Grid.Contains(value), $"{parameter}'s demo value {value} is not on its grid.");
            }
        }
    }
}

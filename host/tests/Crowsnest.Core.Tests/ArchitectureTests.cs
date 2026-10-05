using System.Text.RegularExpressions;

namespace Crowsnest.Core.Tests;

/// <summary>
/// The module boundary (spec §4.1, §5.8), checked on the source. A panel's code stays in its
/// folder under <c>Panels/</c>: Domain and Application never see panels, no panel sees another,
/// and nothing else in <c>src/</c> names a panel family. Only <c>PanelCatalog</c> lists them.
///
/// It reads <c>.cs</c> files rather than reflecting over types, so a panel type used inside a
/// method body is caught too. Comment lines are skipped, so docs may still mention panels.
/// </summary>
public class ArchitectureTests
{
    private static readonly DirectoryInfo Src = FindSrc();
    private static readonly DirectoryInfo Panels = new(Path.Combine(Src.FullName, "Crowsnest.Core", "Panels"));

    private static readonly string[] PanelFolders = [.. Panels.GetDirectories().Select(d => d.Name)];

    [Fact]
    public void DomainAndApplicationDoNotSeePanels()
    {
        string[] offenders = [.. new[] { "Domain", "Application" }
            .SelectMany(layer => SourceFiles(new DirectoryInfo(Path.Combine(Src.FullName, "Crowsnest.Core", layer))))
            .Where(file => Code(file).Any(line => line.Contains("Crowsnest.Core.Panels", StringComparison.Ordinal)))
            .Select(Relative)];

        Assert.True(offenders.Length == 0, $"Domain/ and Application/ must not reference Panels/: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoPanelSeesAnother()
    {
        List<string> offenders = [];
        foreach (string panel in PanelFolders)
        {
            Regex others = Mentions(PanelFolders.Where(p => p != panel));
            offenders.AddRange(SourceFiles(new DirectoryInfo(Path.Combine(Panels.FullName, panel)))
                .Where(file => Code(file).Any(others.IsMatch))
                .Select(Relative));
        }

        Assert.True(offenders.Count == 0, $"A panel must not reference another panel's folder: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NothingOutsidePanelsNamesAPanelFamily()
    {
        Regex any = Mentions(PanelFolders);
        string catalog = Path.Combine(Panels.FullName, "PanelCatalog.cs");

        string[] offenders = [.. SourceFiles(Src)
            .Where(file => !file.FullName.StartsWith(Panels.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => Code(file).Any(any.IsMatch))
            .Select(Relative)];

        Assert.True(File.Exists(catalog), "Panels/PanelCatalog.cs has moved; update this test.");
        Assert.True(offenders.Length == 0,
            $"Only Panels/ and PanelCatalog may name a panel family; move the panel-specific code into its module: {string.Join(", ", offenders)}");
    }

    private static Regex Mentions(IEnumerable<string> panels) =>
        new($@"Crowsnest\.Core\.Panels\.({string.Join('|', panels.Select(Regex.Escape))})\b");

    private static IEnumerable<FileInfo> SourceFiles(DirectoryInfo root) =>
        root.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static IEnumerable<string> Code(FileInfo file) =>
        File.ReadLines(file.FullName).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static string Relative(FileInfo file) => Path.GetRelativePath(Src.FullName, file.FullName);

    private static DirectoryInfo FindSrc()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Crowsnest.slnx")))
            {
                return new DirectoryInfo(Path.Combine(dir.FullName, "src"));
            }
        }

        throw new InvalidOperationException($"No Crowsnest.slnx above {AppContext.BaseDirectory}.");
    }
}

using System.IO;
using System.Text;

namespace Crowsnest.Tray.Tests;

public sealed class FlasherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crowsnest-flasher-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A full image as far as the app description: 0x20000 to the app, the description 0x20 into it.</summary>
    private static byte[] Image(string version, string project = "crowsnest-display")
    {
        byte[] image = new byte[0x20020 + 0x100];
        BitConverter.GetBytes(0xABCD5432).CopyTo(image, 0x20020);
        Encoding.ASCII.GetBytes(version).CopyTo(image, 0x20020 + 0x10);
        Encoding.ASCII.GetBytes(project).CopyTo(image, 0x20020 + 0x30);
        return image;
    }

    private string FlasherFolder(params (string Name, byte[] Contents)[] images)
    {
        string folder = Path.Combine(_root, "app", "flasher");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Flash-Crowsnest.cmd"), "");
        foreach ((string name, byte[] contents) in images)
        {
            File.WriteAllBytes(Path.Combine(folder, name), contents);
        }

        return folder;
    }

    [Fact]
    public void FindsTheFlasherBesideTheAppAndTheNewestImage()
    {
        FlasherFolder(("crowsnest-display-0.4.3.bin", Image("0.4.3")), ("crowsnest-display-0.4.10.bin", Image("0.4.10")));

        Flasher? flasher = Flasher.Find(Path.Combine(_root, "app"));

        Assert.NotNull(flasher);
        Assert.Equal("0.4.10", flasher.FirmwareVersion);
        Assert.EndsWith(Path.Combine("flasher", "Flash-Crowsnest.cmd"), flasher.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsTheRepoFlasherFromTheBuildOutput()
    {
        FlasherFolder();
        string output = Path.Combine(_root, "app", "host", "src", "Crowsnest.Tray", "bin", "Debug");
        Directory.CreateDirectory(output);

        Flasher? flasher = Flasher.Find(output);

        Assert.NotNull(flasher);
        Assert.Null(flasher.FirmwareVersion); // the repo has no image beside the script
    }

    [Fact]
    public void NoFlasherIsNull() => Assert.Null(Flasher.Find(_root));

    [Fact]
    public void AnImageOfAnotherProjectOrTooShortHasNoVersion()
    {
        string folder = FlasherFolder(("crowsnest-display-1.0.0.bin", Image("1.0.0", "something-else")), ("crowsnest-display-2.0.0.bin", new byte[100]));

        Assert.Null(Flasher.ReadVersion(Path.Combine(folder, "crowsnest-display-1.0.0.bin")));
        Assert.Null(Flasher.ReadVersion(Path.Combine(folder, "crowsnest-display-2.0.0.bin")));
        Assert.Null(Flasher.Find(Path.Combine(_root, "app"))!.FirmwareVersion);
    }

    [Theory]
    [InlineData("0.4.2", true)]
    [InlineData("0.3.9", true)]
    [InlineData("0.4.3", false)]
    [InlineData("0.4.3-dirty", false)]
    [InlineData("0.5.0", false)]
    [InlineData("0.1.0-sim", true)]
    [InlineData("garbage", false)]
    public void ABoardIsBehindOnlyWhenItsVersionIsLower(string board, bool behind)
    {
        FlasherFolder(("crowsnest-display-0.4.3.bin", Image("0.4.3")));

        Assert.Equal(behind, Flasher.Find(Path.Combine(_root, "app"))!.IsNewerThan(board));
    }
}

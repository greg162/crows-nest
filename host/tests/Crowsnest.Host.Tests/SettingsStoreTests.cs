using Crowsnest.Core.Application.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crowsnest.Host.Tests;

/// <summary>The settings file on disk (spec §8), in a folder of its own per test.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private const string Device = "a4cb8fdccc6c";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "crowsnest-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "settings.json");

    private SettingsStore Open() => new(NullLogger<SettingsStore>.Instance, FilePath);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static TaskCompletionSource<BridgeSettings> NextChange(SettingsStore store)
    {
        TaskCompletionSource<BridgeSettings> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += settings => changed.TrySetResult(settings);
        return changed;
    }

    [Fact]
    public void AMissingFileIsCreatedWithACommentedStarterThatHasNoDevices()
    {
        using SettingsStore store = Open();

        Assert.True(File.Exists(FilePath));
        Assert.Contains("// Crowsnest settings", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Empty(store.Current.Devices);
    }

    [Fact]
    public void AnExistingFileIsReadNotReplaced()
    {
        Directory.CreateDirectory(_folder);
        const string mine = """{ "devices": { "dccc6c": { "panels": [ "com" ] } } } // mine""";
        File.WriteAllText(FilePath, mine);

        using SettingsStore store = Open();

        Assert.Equal(["com"], store.Current.Find(Device)!.Panels);
        Assert.Equal(mine, File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task AnEditIsPickedUpWhenTheFileIsSaved()
    {
        using SettingsStore store = Open();
        TaskCompletionSource<BridgeSettings> changed = NextChange(store);

        File.WriteAllText(FilePath, """{ "devices": { "dccc6c": { "panels": [ "nav" ], "brightness": 30 } } }""");

        BridgeSettings settings = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(30, settings.Find(Device)!.Brightness);
        Assert.Same(settings, store.Current);
    }

    [Fact]
    public async Task ABrokenEditKeepsTheLastGoodSettings()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """{ "devices": { "dccc6c": { "panels": [ "com" ] } } }""");
        using SettingsStore store = Open();
        TaskCompletionSource<BridgeSettings> changed = NextChange(store);

        File.WriteAllText(FilePath, """{ "devices": { "dccc6c": { "panel": [ "nav" ] } } }""");
        await Task.Delay(800);

        Assert.False(changed.Task.IsCompleted);
        Assert.Equal(["com"], store.Current.Find(Device)!.Panels);
    }

    [Fact]
    public async Task SavingReplacesTheFileWholeAndReadsBack()
    {
        using SettingsStore store = Open();
        TaskCompletionSource<BridgeSettings> changed = NextChange(store);

        store.Save(new BridgeSettings(new Dictionary<string, DeviceSettings> { ["dccc6c"] = new("Radios", ["com", "nav"], 60) }));

        BridgeSettings settings = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["com", "nav"], settings.Find(Device)!.Panels);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task AHandlerThatThrowsDoesNotStopLaterReloads()
    {
        using SettingsStore store = Open();
        store.Changed += _ => throw new InvalidOperationException("handler bug");

        File.WriteAllText(FilePath, """{ "devices": { "dccc6c": { "panels": [ "com" ] } } }""");
        await WaitFor(() => store.Current.Find(Device) is not null);
        TaskCompletionSource<BridgeSettings> changed = NextChange(store);

        File.WriteAllText(FilePath, """{ "devices": { "dccc6c": { "panels": [ "nav" ] } } }""");

        Assert.Equal(["nav"], (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Find(Device)!.Panels);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}

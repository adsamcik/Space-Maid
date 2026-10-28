using System.IO;
using System.Text.Json;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ExtensionWorkspaceTests
{
    [Fact]
    public async Task EnableChangesArePendingUntilRestartAndCanBeReverted()
    {
        using var fixture = new TestDirectory();
        string bundles = Path.Join(fixture.Path, "bundles"), data = Path.Join(fixture.Path, "data");
        MakeExtension(bundles, "test.disabled");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Join(data, "extensions.json"), JsonSerializer.Serialize(new ExtensionSettings([], ["test.disabled"])));
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, data, bundles,
            Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeAsync();
        var entry = Assert.Single(runtime.Entries);
        Assert.Equal("1.0.0", entry.Version);
        Assert.False(runtime.HasPendingChange(entry));
        runtime.SetEnabled(entry, true);
        Assert.True(runtime.HasPendingChange(entry)); Assert.Null(entry.View);
        runtime.SetEnabled(entry, false);
        Assert.False(runtime.HasPendingChange(entry));
    }

    [Fact]
    public async Task AddingDuplicateIdsIsRejectedWithoutChangingSettings()
    {
        using var fixture = new TestDirectory();
        string data = Path.Join(fixture.Path, "data");
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, data, Path.Join(fixture.Path, "bundles"),
            Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeAsync();
        string first = MakeExtension(Path.Join(fixture.Path, "first"), "test.new");
        string duplicate = MakeExtension(Path.Join(fixture.Path, "second"), "test.new");
        runtime.AddTrustedDirectory(first);
        string original = File.ReadAllText(Path.Join(data, "extensions.json"));
        Assert.False(runtime.TryAddTrustedDirectory(duplicate, out _, out var error));
        Assert.Contains("ID is already installed", error);
        Assert.Equal(original, File.ReadAllText(Path.Join(data, "extensions.json")));
    }

    [Fact]
    public async Task InvalidManifestRemainsALoadFailureWithoutPendingEnable()
    {
        using var fixture = new TestDirectory();
        string bundles = Path.Join(fixture.Path, "bundles");
        string path = MakeExtension(bundles, "test.invalid");
        File.WriteAllText(Path.Join(path, "extension.json"), JsonSerializer.Serialize(new ExtensionManifest("test.invalid", "Invalid", "1.0.0", 99, "entry.dll", "Test.Extension")));
        await using var runtime = new ExtensionRuntime(() => null, () => false, _ => { }, Path.Join(fixture.Path, "data"), bundles,
            Path.Join(fixture.Path, "logs"), administrator: false);
        await runtime.InitializeAsync();
        var entry = Assert.Single(runtime.Entries);
        Assert.Equal("Failed to load", entry.Status);
        Assert.False(runtime.HasPendingChange(entry));
    }

    private static string MakeExtension(string root, string id)
    {
        string path = Directory.CreateDirectory(Path.Join(root, id)).FullName;
        File.WriteAllText(Path.Join(path, "extension.json"), JsonSerializer.Serialize(new ExtensionManifest(id, id, "1.0.0", 1, "entry.dll", "Test.Extension")));
        File.WriteAllBytes(Path.Join(path, "entry.dll"), []);
        File.WriteAllText(Path.Join(path, "entry.deps.json"), "{}");
        return path;
    }
}

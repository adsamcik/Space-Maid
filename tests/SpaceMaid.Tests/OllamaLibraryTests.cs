using System.IO;
using System.Text;
using System.Text.Json;
using SpaceMaid.Core.IO;
using SpaceMaid.Extensions.Opportunities;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OllamaLibraryTests
{
    internal static byte[] Manifest(long bytes, bool cloud = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 2,
        config = new { size = 512, digest = "sha256:" + new string('a', 64) },
        layers = cloud ? null : new[] { new { mediaType = "application/vnd.ollama.image.model", size = bytes, digest = "sha256:" + new string('b', 64) } }
    });
    internal static string WriteModel(string library, string name, long bytes, string registry = "registry.ollama.ai", string space = "library")
    {
        string path = Path.Join(library, "manifests", registry, space, name.Replace(':', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Manifest(bytes));
        return path;
    }
    [Fact]
    public void SelectedLibraryListsTagsNamespacesAndSizesWithoutReadingSharedBlobs()
    {
        using var fixture = new TestDirectory();
        string library = Path.Join(fixture.Path, "relocated-store");
        WriteModel(library, "gemma3:12b", 8L << 30);
        WriteModel(library, "gemma3:alias", 8L << 30);
        WriteModel(library, "custom:latest", 4096, space: "research");
        WriteModel(library, "remote:tag", 2048, registry: "example.org", space: "team");
        string cloud = WriteModel(library, "cloud:latest", 0);
        File.WriteAllBytes(cloud, Manifest(0, cloud: true));
        string blob = Path.Join(library, "blobs", "sha256-" + new string('b', 64));
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllText(blob, "owned fixture; must never be opened");
        using var lockedBlob = File.Open(blob, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = OllamaLibrary.Read(new DiskAccess(() => null), library, default);
        Assert.Equal(5, result.Models.Count); Assert.Empty(result.Note);
        Assert.Equal(2, result.Models.Count(model => model.Bytes == (8L << 30) + 512));
        Assert.Contains(result.Models, model => model.Name == "research/custom:latest" && model.Bytes == 4608);
        Assert.Contains(result.Models, model => model.Name == "example.org/team/remote:tag");
        Assert.Contains(result.Models, model => model.Name == "cloud:latest" && model.Bytes == 512);
        Assert.Equal((8L << 30) + 512, result.Models[0].Bytes);
    }
    [Fact]
    public void CorruptAndOversizedManifestsKeepNamesWithUnknownSizesAndReportPartialDetails()
    {
        using var fixture = new TestDirectory();
        WriteModel(fixture.Path, "valid:tag", 1024);
        string malformed = WriteModel(fixture.Path, "incomplete:latest", 0);
        File.WriteAllText(malformed, "{\"schemaVersion\":\"2\"}");
        string oversized = WriteModel(fixture.Path, "large:latest", 0);
        File.WriteAllBytes(oversized, new byte[OllamaLibrary.MaximumManifestBytes + 1]);
        var result = OllamaLibrary.Read(new DiskAccess(() => null), fixture.Path, default);
        Assert.Equal(3, result.Models.Count);
        Assert.Single(result.Models, model => model.Bytes == 1536);
        Assert.Equal(2, result.Models.Count(model => model.Bytes is null && model.SizeText == "Unavailable"));
        Assert.Contains("sizes are unavailable", result.Note);
        Assert.Throws<OperationCanceledException>(() => OllamaLibrary.Read(new(), fixture.Path, new(true)));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"config\":{},\"layers\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"config\":{\"size\":-1},\"layers\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"config\":{\"size\":\"1\"},\"layers\":[]}")]
    public void InvalidSizeReportsAreNeverShownAsZero(string json)
        => Assert.Throws<InvalidDataException>(() => OllamaLibrary.ManifestSize(Encoding.UTF8.GetBytes(json)));
}

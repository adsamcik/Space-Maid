using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using SpaceMaid.Installer;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class InstallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableExtractionPreservesUpdateLayoutAndSettingsMarker(bool existingEmptyFolder)
    {
        using var directory = new TestDirectory();
        string destination = Path.Join(directory.Path, "Portable");
        if (existingEmptyFolder) Directory.CreateDirectory(destination);
        using MemoryStream archive = CreateArchive(("current/sample.txt", "photo library"));
        string actual = await PortableInstaller.ExtractAsync(archive, Digest(archive), destination);

        Assert.Equal(destination, actual);
        Assert.Equal("photo library", await File.ReadAllTextAsync(Path.Join(destination, "current", "sample.txt")));
        Assert.True(File.Exists(Path.Join(destination, ".portable")));
        Assert.True(File.Exists(Path.Join(destination, "Update.exe")));
        Assert.True(File.Exists(Path.Join(destination, "current", "sq.version")));
        Assert.True(File.Exists(Path.Join(destination, "current", "SpaceMaid.App.exe")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(directory.Path), path => path != destination);
    }

    [Fact]
    public async Task ExistingDataIsNeverOverwritten()
    {
        using var directory = new TestDirectory();
        string destination = Path.Join(directory.Path, "Portable");
        Directory.CreateDirectory(destination);
        string content = Path.Join(destination, "notes.txt");
        await File.WriteAllTextAsync(content, "keep this");
        using MemoryStream archive = CreateArchive();

        await Assert.ThrowsAsync<IOException>(() => PortableInstaller.ExtractAsync(archive, Digest(archive), destination));
        Assert.Equal("keep this", await File.ReadAllTextAsync(content));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task DamagedPayloadLeavesEmptyDestinationUntouched()
    {
        using var directory = new TestDirectory();
        string destination = Path.Join(directory.Path, "Portable");
        Directory.CreateDirectory(destination);
        using MemoryStream archive = CreateArchive();

        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.ExtractAsync(archive, new string('0', 64), destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Single(Directory.EnumerateDirectories(directory.Path));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("current/../../escape.txt")]
    [InlineData("/escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("current/file.txt:stream")]
    [InlineData("current/CON.txt")]
    [InlineData("current/space .")]
    [InlineData("current/./file.txt")]
    [InlineData("current\\..\\escape.txt")]
    public async Task UnsafeArchiveNamesAreRejectedBeforeWriting(string name)
    {
        using var directory = new TestDirectory();
        using MemoryStream archive = CreateArchive((name, "unsafe"));

        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.ExtractAsync(archive, Digest(archive), Path.Join(directory.Path, "Portable")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DuplicateAndLinkedArchiveEntriesAreRejected()
    {
        using var directory = new TestDirectory();
        using MemoryStream duplicate = CreateArchive(("current/spaceMAID.app.EXE", "duplicate"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.ExtractAsync(duplicate, Digest(duplicate), Path.Join(directory.Path, "Portable")));

        using MemoryStream linked = CreateArchive(("current/link", "target"));
        using (var zip = new ZipArchive(linked, ZipArchiveMode.Update, leaveOpen: true))
            zip.GetEntry("current/link")!.ExternalAttributes = 0xa000 << 16;
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.ExtractAsync(linked, Digest(linked), Path.Join(directory.Path, "Portable")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task CancellationRemovesOnlyOwnedStagingAndPreservesChosenFolder()
    {
        using var directory = new TestDirectory();
        string destination = Path.Join(directory.Path, "Portable");
        Directory.CreateDirectory(destination);
        string sibling = Path.Join(directory.Path, "keep.txt");
        await File.WriteAllTextAsync(sibling, "keep");
        using MemoryStream archive = CreateArchive(("current/large.txt", new string('x', 1024 * 1024)));
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(() => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PortableInstaller.ExtractAsync(archive, Digest(archive), destination, progress, cancellation.Token));
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Equal("keep", await File.ReadAllTextAsync(sibling));
        Assert.Single(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public async Task ConcurrentDestinationWritesRemainUntouched()
    {
        using var directory = new TestDirectory();
        string destination = Path.Join(directory.Path, "Portable");
        Directory.CreateDirectory(destination);
        using MemoryStream archive = CreateArchive(("current/sample.txt", "some data"));
        var progress = new CallbackProgress(() => File.WriteAllText(Path.Join(destination, "new.txt"), "another app"));

        await Assert.ThrowsAsync<IOException>(() => PortableInstaller.ExtractAsync(archive, Digest(archive), destination, progress));
        Assert.Equal("another app", await File.ReadAllTextAsync(Path.Join(destination, "new.txt")));
        Assert.Single(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public void DriveRootsAndRelativePathsCannotBeDestinations()
    {
        Assert.Throws<IOException>(() => PortableInstaller.ValidateDestination(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.Throws<IOException>(() => PortableInstaller.ValidateDestination("Portable"));
    }

    private static MemoryStream CreateArchive(params (string Name, string Contents)[] additional)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in new[] { (".portable", ""), ("Update.exe", "update"), ("Space Maid.exe", "launcher"), ("current/SpaceMaid.App.exe", "app"), ("current/sq.version", "version") }.Concat(additional))
            {
                using var writer = new StreamWriter(zip.CreateEntry(item.Item1).Open());
                writer.Write(item.Item2);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Digest(MemoryStream stream) => Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    private sealed class CallbackProgress(Action callback) : IProgress<double> { public void Report(double value) => callback(); }
}

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using SpaceMaid.Installer;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class InstallerTests
{
    [Fact]
    public void ExistingInstallationUsesManifestVersionAndRegisteredLocation()
    {
        using var directory = new TestDirectory();
        string registered = CreateInstallation(directory.Path, "Custom installation", "0.1.0-alpha.2");
        string fallback = CreateInstallation(directory.Path, "Default", "0.1.0");

        InstalledApplication installation = Assert.IsType<InstalledApplication>(InstallationDiscovery.Find([registered, fallback]));
        Assert.Equal(registered, installation.Folder);
        Assert.Equal("0.1.0-alpha.2", installation.Version.ToString());
        Assert.True(installation.NeedsUpdate("0.1.0-beta.1"));
    }

    [Fact]
    public void StaleRegistrationFallsBackToVerifiedDefaultInstallation()
    {
        using var directory = new TestDirectory();
        string fallback = CreateInstallation(directory.Path, "Default", "0.1.0-beta.1");

        Assert.Equal(fallback, InstallationDiscovery.Find([null, "relative", Path.Join(directory.Path, "Missing"), fallback])?.Folder);
        Assert.Null(InstallationDiscovery.Find([Path.Join(directory.Path, "Missing")]));
    }

    [Theory]
    [InlineData("0.1.0-alpha.2", "0.1.0-beta.1", true)]
    [InlineData("0.1.0-beta.1", "0.1.0-beta.1", false)]
    [InlineData("0.1.0", "0.1.0-beta.1", false)]
    [InlineData("0.2.0", "0.1.0", false)]
    [InlineData("0.1.0-beta.2", "0.1.0-beta.10", true)]
    [InlineData("0.1.0+old", "0.1.0+new", false)]
    public void UpdateChoiceRespectsSemanticVersionOrder(string installed, string setup, bool update)
        => Assert.Equal(update, new InstalledApplication("unused", Velopack.SemanticVersion.Parse(installed)).NeedsUpdate(setup));

    [Theory]
    [InlineData("portable")]
    [InlineData("updater")]
    [InlineData("app")]
    [InlineData("manifest")]
    [InlineData("wrong-product")]
    [InlineData("wrong-executable")]
    [InlineData("invalid-version")]
    [InlineData("invalid-xml")]
    [InlineData("dtd")]
    public void UnverifiedAndPortableFoldersAreNotNormalInstallations(string damage)
    {
        using var directory = new TestDirectory();
        string folder = CreateInstallation(directory.Path, "Installation", "0.1.0-alpha.2");
        string manifest = Path.Join(folder, "current", "sq.version");
        switch (damage)
        {
            case "portable": File.WriteAllText(Path.Join(folder, ".portable"), ""); break;
            case "updater": File.Delete(Path.Join(folder, "Update.exe")); break;
            case "app": File.Delete(Path.Join(folder, "current", "SpaceMaid.App.exe")); break;
            case "manifest": File.Delete(manifest); break;
            case "wrong-product": File.WriteAllText(manifest, Manifest("0.1.0", "AnotherApp")); break;
            case "wrong-executable": File.WriteAllText(manifest, Manifest("0.1.0").Replace("SpaceMaid.App.exe", "Other.exe")); break;
            case "invalid-version": File.WriteAllText(manifest, Manifest("unknown")); break;
            case "invalid-xml": File.WriteAllText(manifest, "broken"); break;
            case "dtd": File.WriteAllText(manifest, "<!DOCTYPE package [<!ENTITY version '0.1.0'>]>" + Manifest("&version;")); break;
        }

        Assert.Null(InstallationDiscovery.Read(folder));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void UpdateTargetsExistingFolderAndExactPackageWithoutReinstalling()
    {
        var installation = new InstalledApplication(@"C:\Custom Apps\Space Maid", Velopack.SemanticVersion.Parse("0.1.0-alpha.2"));
        var command = installation.UpdateCommand(@"C:\Setup Files\Update.nupkg");

        Assert.Equal(@"C:\Custom Apps\Space Maid\Update.exe", command.FileName);
        Assert.Equal(new[] { "apply", "--rootDir", installation.Folder, "--package", @"C:\Setup Files\Update.nupkg", "--norestart" }, command.ArgumentList);
    }

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
    private static string CreateInstallation(string parent, string name, string version)
    {
        string folder = Path.Join(parent, name);
        Directory.CreateDirectory(Path.Join(folder, "current"));
        File.WriteAllText(Path.Join(folder, "Update.exe"), "updater");
        File.WriteAllText(Path.Join(folder, "current", "SpaceMaid.App.exe"), "app");
        File.WriteAllText(Path.Join(folder, "current", "sq.version"), Manifest(version));
        return folder;
    }
    private static string Manifest(string version, string id = "SpaceMaid")
        => $"<package xmlns='http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd'><metadata><id>{id}</id><version>{version}</version><mainExe>SpaceMaid.App.exe</mainExe></metadata></package>";
    private sealed class CallbackProgress(Action callback) : IProgress<double> { public void Report(double value) => callback(); }
}

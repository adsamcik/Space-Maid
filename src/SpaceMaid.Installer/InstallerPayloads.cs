using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace SpaceMaid.Installer;

internal sealed class InstallerPayloads(Assembly assembly)
{
    private const string SetupResource = "SpaceMaid.Installer.Payload.Setup.exe";
    private const string PortableResource = "SpaceMaid.Installer.Payload.Portable.zip";
    public string Version { get; } = (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Development build").Split('+')[0];
    public string PortableSha256 => Metadata("PortableSha256");
    public bool IsAvailable => PayloadIntegrity.IsSha256(Metadata("SetupSha256")) && PayloadIntegrity.IsSha256(PortableSha256)
        && assembly.GetManifestResourceNames().Contains(SetupResource) && assembly.GetManifestResourceNames().Contains(PortableResource);

    private string Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == key)?.Value ?? "";
    public Stream OpenPortable() => assembly.GetManifestResourceStream(PortableResource) ?? throw new InvalidDataException("Installation files are missing. Download Setup again.");

    public async Task<PreparedSetup> PrepareSetupAsync(CancellationToken cancellationToken)
    {
        string directory = Path.Join(Path.GetTempPath(), "SpaceMaid-Setup-" + Guid.NewGuid().ToString("N"));
        SafeDirectories.EnsureNoReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        var prepared = new PreparedSetup(directory);
        try
        {
            using Stream source = assembly.GetManifestResourceStream(SetupResource) ?? throw new InvalidDataException("Installation files are missing. Download Setup again.");
            await using (var destination = new FileStream(prepared.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await source.CopyToAsync(destination, cancellationToken);
            await using (var payload = new FileStream(prepared.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                await PayloadIntegrity.VerifyAsync(payload, Metadata("SetupSha256"), cancellationToken);
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
}

internal sealed class PreparedSetup(string directory) : IDisposable
{
    public string Path { get; } = System.IO.Path.Join(directory, "Setup.exe");
    public void Dispose() => SafeDirectories.TryDeleteOwnedDirectory(directory);
}

internal static class PayloadIntegrity
{
    public static bool IsSha256(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    public static async Task VerifyAsync(Stream payload, string expectedHash, CancellationToken cancellationToken)
    {
        if (!IsSha256(expectedHash)) throw new InvalidDataException("Installation files could not be verified. Download Setup again.");
        byte[] actual = await SHA256.HashDataAsync(payload, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash)))
            throw new InvalidDataException("Installation files are damaged. Download Setup again.");
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SpaceMaid.Core.Optimization;

internal sealed class ProofBudget(long bytes, TimeSpan duration)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public long Remaining { get; private set; } = bytes;
    public bool Exhausted { get; private set; }
    public void Spend(long count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > Remaining || _clock.Elapsed > duration)
        { Exhausted = true; throw new InvalidOperationException("Inspection budget reached; remaining evidence is unknown."); }
        Remaining -= count;
    }
}

internal sealed class GeneratedResourceProof : IDisposable
{
    private readonly List<GeneratedReadLease> _inputs = [];
    private readonly ProofBudget _budget;
    private readonly CancellationToken _token;
    private readonly HashSet<string> _mypyModules = new(StringComparer.OrdinalIgnoreCase);
    private GeneratedResourceProof(ProofBudget budget, CancellationToken token) { _budget = budget; _token = token; }
    public IReadOnlyList<GeneratedInputRevision> Inputs => _inputs.Select(i => i.Revision).ToArray();
    public string ContentHash { get; private set; } = "";
    public OptimizationEvidence Evidence { get; private set; } = null!;

    public static GeneratedResourceProof Verify(OptimizationFile file, Stream stream, GeneratedInputRevision target,
        ProofBudget budget, CancellationToken token)
    {
        var proof = new GeneratedResourceProof(budget, token);
        try
        {
            byte[] content = proof.Read(stream);
            proof.ContentHash = Convert.ToHexString(SHA256.HashData(content));
            string producer, owner = file.ProviderId;
            switch (file.Rule.ResourceKind)
            {
                case GeneratedResourceKind.PythonBytecode: proof.Python(file, content, target); producer = "CPython validated bytecode"; break;
                case GeneratedResourceKind.DotNetCompilerCache: proof.DotNet(file, content); producer = "MSBuild compiler input fingerprint"; break;
                case GeneratedResourceKind.MypyIncremental: proof.Mypy(file, content); producer = "mypy JSON incremental cache"; break;
                case GeneratedResourceKind.OwnedCache: proof.Owned(file, target); producer = "Recorded generated resource"; break;
                default: throw new InvalidOperationException("No verified producer contract; review this resource manually.");
            }
            foreach (var input in proof._inputs) input.Verify();
            if (file.Inputs.Count > 0 && !file.Inputs.SequenceEqual(proof.Inputs)) throw new IOException("Producer inputs changed after preview; kept.");
            if (file.ContentHash is { } expected && expected != proof.ContentHash) throw new IOException("Generated content changed after preview; kept.");
            proof.Evidence = new(OptimizationAction.EvictGeneratedFile, OptimizationDisposition.Eligible, owner, file.Path,
                "Producer contract and current readable inputs verified.", RestorationRequirement.Recompute, DateTime.UtcNow)
            { Producer = producer, Variant = Path.GetRelativePath(file.ContainerPath, Path.GetDirectoryName(file.Path)!),
                ContractVersion = 2, CoverageComplete = true, RequiredInputs = proof.Inputs.Select(i => i.Path).ToArray() };
            return proof;
        }
        catch { proof.Dispose(); throw; }
    }

    private GeneratedReadLease Input(string path, bool preserveAccessTime = false)
    {
        _budget.Spend(0, _token);
        path = OptimizationPolicy.NormalizePath(path);
        var existing = _inputs.FirstOrDefault(i => i.Revision.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        if (_inputs.Count >= 16) throw new IOException("Producer input limit reached; review this resource.");
        var lease = GeneratedReadLease.Open(path, preserveAccessTime); _inputs.Add(lease); return lease;
    }

    private byte[] Read(Stream stream)
    {
        if (stream.Length > 8 * 1024 * 1024) throw new IOException("Producer evidence exceeds the per-resource read budget.");
        _budget.Spend(stream.Length, _token); stream.Position = 0;
        byte[] data = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < data.Length)
        { _budget.Spend(0, _token); int count = stream.Read(data, offset, Math.Min(65536, data.Length - offset)); if (count == 0) throw new EndOfStreamException(); offset += count; }
        return data;
    }

    private void Python(OptimizationFile file, byte[] data, GeneratedInputRevision target)
    {
        string sourcePath = file.Rule.RequiredSourcePath(file.Path, file.ContainerPath) ?? throw new IOException("Unrecognized bytecode layout; kept.");
        var tag = Regex.Match(Path.GetFileName(file.Path), @"\.cpython-(311|312|313|314)(?:\.opt-[012])?\.pyc$");
        if (!tag.Success || data.Length <= 16 || (data[16] & 0x7f) != 'c') throw new IOException("Unrecognized Python code cache format; kept.");
        ushort magic = tag.Groups[1].Value switch { "311" => 3495, "312" => 3531, "313" => 3571, "314" => 3627, _ => 0 };
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) != magic || data[2] != 13 || data[3] != 10)
            throw new IOException("Python cache version does not match its filename; kept.");
        var source = Input(sourcePath); byte[] sourceBytes = Read(source.Stream);
        // A source edited after the cache (even with restored write times) is
        // positive mismatch evidence. No interpreter/project code is executed.
        if (source.Revision.Changed > target.Changed) throw new IOException("Source changed after cached bytecode; kept.");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (flags == 0)
        {
            long seconds = new DateTimeOffset(DateTime.FromFileTimeUtc(source.Revision.Written)).ToUnixTimeSeconds();
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != unchecked((uint)seconds)
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) != sourceBytes.Length)
                throw new IOException("Python cache does not match current source metadata; kept.");
        }
        else if (flags == 3)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)) != PythonSourceHash(key, sourceBytes))
                throw new IOException("Python cache source hash does not match; kept.");
        }
        else throw new IOException("Unchecked or unknown Python cache validation mode; review manually.");
    }

    private void DotNet(OptimizationFile file, byte[] data)
    {
        string[] parts = Path.GetRelativePath(file.ContainerPath, file.Path).Split(Path.DirectorySeparatorChar);
        if (parts.Length is < 3 or > 4 || parts[0] is not ("Debug" or "Release")
            || !Regex.IsMatch(parts[1], @"^net(?:standard|coreapp)?[0-9][A-Za-z0-9.\-]*$"))
            throw new IOException("Unknown build variant/layout; review manually.");
        string fingerprint = Encoding.ASCII.GetString(data).TrimEnd('\r', '\n');
        if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit)) throw new IOException("Not an MSBuild input fingerprint; kept.");
        string fileName = Path.GetFileName(file.Path);
        string suffix = fileName.EndsWith(".AssemblyInfoInputs.cache", StringComparison.Ordinal) ? ".AssemblyInfoInputs.cache" : ".CoreCompileInputs.cache";
        string stem = fileName[..^suffix.Length];
        string projectRoot = Path.GetDirectoryName(file.ContainerPath)!;
        string project;
        if (suffix == ".CoreCompileInputs.cache") project = Path.Join(projectRoot, stem);
        else
        {
            var choices = new[] { ".csproj", ".fsproj", ".vbproj" }.Select(ext => Path.Join(projectRoot, stem + ext)).Where(File.Exists).ToArray();
            if (choices.Length != 1) throw new IOException("The build resource has no single project owner; kept.");
            project = choices[0];
        }
        if (Path.GetExtension(project) is not (".csproj" or ".fsproj" or ".vbproj")) throw new IOException("Unknown project owner; kept.");
        var projectFile = Input(project);
        using var xml = System.Xml.XmlReader.Create(new MemoryStream(Read(projectFile.Stream)),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(xml).Root;
        if (root is null || root.Name.LocalName != "Project" || root.Attribute("Sdk")?.Value is not
            ("Microsoft.NET.Sdk" or "Microsoft.NET.Sdk.Web" or "Microsoft.NET.Sdk.Worker" or "Microsoft.NET.Sdk.WindowsDesktop" or "Microsoft.NET.Sdk.Razor"))
            throw new IOException("An SDK project owner is required; review manually.");
        string manifestPath = Path.Join(Path.GetDirectoryName(file.Path), Path.GetFileName(project) + ".FileListAbsolute.txt");
        var manifest = Input(manifestPath);
        string[] outputs = Encoding.UTF8.GetString(Read(manifest.Stream)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (outputs.Length > 20000 || !outputs.Any(p => string.Equals(p, file.Path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Exact output is absent from the producer's build manifest; kept.");
    }

    private void Mypy(OptimizationFile file, byte[] data)
    {
        bool isMeta = file.Path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase);
        string stem = file.Path[..^(isMeta ? ".meta.json".Length : ".data.json".Length)];
        if (!_mypyModules.Add(stem)) return;
        byte[] metaBytes = isMeta ? data : Read(Input(stem + ".meta.json").Stream);
        byte[] cacheBytes = isMeta ? Read(Input(stem + ".data.json").Stream) : data;
        using var meta = JsonDocument.Parse(metaBytes, new JsonDocumentOptions { MaxDepth = 32 });
        using var cache = JsonDocument.Parse(cacheBytes, new JsonDocumentOptions { MaxDepth = 64 });
        var m = meta.RootElement; var c = cache.RootElement;
        if (m.ValueKind != JsonValueKind.Object || c.ValueKind != JsonValueKind.Object
            || !m.TryGetProperty("version_id", out var version) || version.GetString()?.StartsWith("1.", StringComparison.Ordinal) != true
            || !m.TryGetProperty("id", out var id) || !m.TryGetProperty("path", out var path)
            || !c.TryGetProperty(".class", out var kind) || kind.GetString() != "MypyFile"
            || !c.TryGetProperty("_fullname", out var fullname) || fullname.GetString() != id.GetString()
            || !m.TryGetProperty("mtime", out var mtime) || !m.TryGetProperty("size", out var size)
            || !m.TryGetProperty("hash", out var hash) || hash.GetString() is not { Length: > 0 }
            || !m.TryGetProperty("interface_hash", out var dataHash)
            || !string.Equals(dataHash.GetString(), Convert.ToHexString(SHA1.HashData(cacheBytes)), StringComparison.OrdinalIgnoreCase)
            || !m.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
            throw new IOException("Unrecognized mypy producer records; kept.");
        string projectRoot = Path.GetDirectoryName(file.ContainerPath)!;
        string sourcePath = Path.GetFullPath(path.GetString() ?? "", projectRoot);
        if (!OptimizationPolicy.Covers(projectRoot, sourcePath)) throw new IOException("Mypy input is outside this project; review manually.");
        var source = Input(sourcePath);
        string sourceHash = Convert.ToHexString(SHA1.HashData(Read(source.Stream)));
        double seconds = new DateTimeOffset(DateTime.FromFileTimeUtc(source.Revision.Written)).ToUnixTimeMilliseconds() / 1000d;
        if (size.GetInt64() != source.Revision.Length || Math.Abs(mtime.GetDouble() - seconds) > 1
            || !string.Equals(hash.GetString(), sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Mypy input revision changed; kept.");
        string relative = Path.GetRelativePath(file.ContainerPath, file.Path);
        string versionRoot = Path.Join(file.ContainerPath, relative.Split(Path.DirectorySeparatorChar)[0]);
        foreach (var dependency in dependencies.EnumerateArray())
        {
            string? module = dependency.GetString();
            if (module is null || !Regex.IsMatch(module, @"^[A-Za-z_][A-Za-z0-9_.]*$")) throw new IOException("Unknown mypy dependency; review manually.");
            string direct = Path.Join(versionRoot, module.Replace('.', Path.DirectorySeparatorChar) + ".data.json");
            string package = Path.Join(versionRoot, module.Replace('.', Path.DirectorySeparatorChar), "__init__.data.json");
            var choices = new[] { direct, package }.Where(File.Exists).ToArray();
            if (choices.Length != 1) throw new IOException("Mypy dependency evidence is missing or ambiguous; kept.");
            if (_mypyModules.Contains(choices[0][..^".data.json".Length])) continue;
            var dependent = Input(choices[0], preserveAccessTime: true);
            Mypy(file with { Path = choices[0] }, Read(dependent.Stream));
        }
    }

    private void Owned(OptimizationFile file, GeneratedInputRevision target)
    {
        var record = JsonSerializer.Deserialize<GeneratedOwnershipRecord>(Read(Input(GeneratedFileOwnership.ReceiptPath(file.Path)).Stream))
            ?? throw new IOException("Generated ownership receipt is unavailable.");
        if (record.Version != 2 || record.Owner != file.ProviderId || record.FileName != Path.GetFileName(file.Path)
            || record.Output is null || string.IsNullOrWhiteSpace(record.Output.Path) || !record.Output.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)
            || record.Output.Volume != target.Volume || record.Output.Identity != target.Identity || record.Output.Length != target.Length
            || record.Sha256 != ContentHash || record.Inputs is null || record.Inputs.Count is < 1 or > 14)
            throw new IOException("The exact file has no matching producer ownership record; kept.");
        foreach (var revision in record.Inputs)
            if (revision is null || string.IsNullOrWhiteSpace(revision.Path) || Input(revision.Path).Revision != revision)
                throw new IOException("The original needed to recreate this resource changed or is unavailable; kept.");
    }

    // CPython's _Py_KeyedHash uses SipHash-1-3 with (magic, 0).
    internal static ulong PythonSourceHash(uint key, ReadOnlySpan<byte> bytes)
    {
        ulong v0 = key ^ 0x736f6d6570736575UL, v1 = 0x646f72616e646f6dUL, v2 = key ^ 0x6c7967656e657261UL, v3 = 0x7465646279746573UL;
        int offset = 0;
        void Round()
        {
            v0 = unchecked(v0 + v1); v1 = BitOperations.RotateLeft(v1, 13) ^ v0; v0 = BitOperations.RotateLeft(v0, 32);
            v2 = unchecked(v2 + v3); v3 = BitOperations.RotateLeft(v3, 16) ^ v2;
            v0 = unchecked(v0 + v3); v3 = BitOperations.RotateLeft(v3, 21) ^ v0;
            v2 = unchecked(v2 + v1); v1 = BitOperations.RotateLeft(v1, 17) ^ v2; v2 = BitOperations.RotateLeft(v2, 32);
        }
        while (offset + 8 <= bytes.Length) { ulong m = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]); offset += 8; v3 ^= m; Round(); v0 ^= m; }
        ulong end = (ulong)bytes.Length << 56;
        for (int n = 0; offset < bytes.Length; n++, offset++) end |= (ulong)bytes[offset] << (n * 8);
        v3 ^= end; Round(); v0 ^= end; v2 ^= 0xff; Round(); Round(); Round(); return v0 ^ v1 ^ v2 ^ v3;
    }

    public void Dispose() { for (int i = _inputs.Count - 1; i >= 0; i--) _inputs[i].Dispose(); }
}

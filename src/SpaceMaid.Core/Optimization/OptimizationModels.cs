using System.Security.Cryptography;
using System.Text;

namespace SpaceMaid.Core.Optimization;

// Contributions describe generated files, never supply an execution callback.
// Changing file patterns or consequences requires fresh per-rule consent.
public sealed record AutomaticCleanupRule
{
    public string Id { get; }
    public string Name { get; }
    public string Regeneration { get; }
    public IReadOnlyList<string> FileSuffixes { get; }
    public int MinimumAgeDays { get; }
    public bool RequiresPythonSource { get; }

    public AutomaticCleanupRule(string id, string name, string regeneration, IEnumerable<string> fileSuffixes, int minimumAgeDays = 7, bool requiresPythonSource = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'))
            throw new ArgumentException("Invalid optimization rule ID.", nameof(id));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || string.IsNullOrWhiteSpace(regeneration) || regeneration.Length > 1024)
            throw new ArgumentException("Name and regeneration instructions are required.");
        var suffixes = fileSuffixes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (suffixes.Length is < 1 or > 32 || suffixes.Any(s => s.Length is < 2 or > 80 || s[0] != '.'
            || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_')))
            throw new ArgumentException("Use explicit generated-file suffixes.", nameof(fileSuffixes));
        if (minimumAgeDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(minimumAgeDays));
        Id = id; Name = name; Regeneration = regeneration; FileSuffixes = Array.AsReadOnly(suffixes); MinimumAgeDays = minimumAgeDays;
        RequiresPythonSource = requiresPythonSource;
    }

    public string ConsentKey(string providerId)
    {
        string signature = string.Join('\n', FileSuffixes.Select(s => s.ToLowerInvariant())) + "\n" + Regeneration + "\n" + MinimumAgeDays + "\n" + RequiresPythonSource;
        return providerId + "/" + Id + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
    }

    public bool Matches(string name) => FileSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    internal string? RequiredSourcePath(string file, string container)
    {
        if (!RequiresPythonSource) return null;
        if (!string.Equals(Path.GetDirectoryName(file), container, StringComparison.OrdinalIgnoreCase)) return null;
        string stem = Path.GetFileNameWithoutExtension(file);
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"\.cpython-[0-9]+[a-z]*(?:\.opt-[0-9]+)?$", "");
        string name = stem + ".py";
        return Path.Join(Path.GetDirectoryName(container), name);
    }
}

public sealed record OptimizationPolicy
{
    public bool Enabled { get; init; }
    public string ScopePath { get; init; } = "";
    public string[] EnabledRules { get; init; } = [];
    public string[] ExcludedPaths { get; init; } = [];
    public int KeepRecentDays { get; init; } = 14;
    public long MaxBytesPerRun { get; init; } = 512L * 1024 * 1024;
    public int MaxFilesPerRun { get; init; } = 500;

    public OptimizationPolicy Validate()
    {
        if (KeepRecentDays is < 1 or > 365 || MaxFilesPerRun is < 1 or > 5000 || MaxBytesPerRun is < 1 or > 10L * 1024 * 1024 * 1024)
            throw new ArgumentException("Invalid optimization retention or run limit.");
        if (EnabledRules is null || ExcludedPaths is null || EnabledRules.Length > 256 || ExcludedPaths.Length > 256
            || EnabledRules.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512)) throw new ArgumentException("Invalid optimization rules.");
        string scope = string.IsNullOrEmpty(ScopePath) && !Enabled ? "" : NormalizePath(ScopePath);
        return this with { ScopePath = scope, EnabledRules = EnabledRules.Distinct(StringComparer.Ordinal).ToArray(),
            ExcludedPaths = ExcludedPaths.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith('\\')
            || path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/')
            || path[3..].Split(['\\', '/']).Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Choose an absolute local folder or drive.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Covers(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record OptimizationFile(string Path, string ContainerPath, string RuleKey, long LogicalBytes,
    long EstimatedBytes, long LastWriteFileTime, long CreationFileTime, SpaceMaid.Core.Scanning.FileIdentity? Identity,
    AutomaticCleanupRule Rule)
{
    public ulong? VolumeSerialNumber { get; init; }
}
public sealed record OptimizationSkipped(string Path, string Reason);
public sealed record OptimizationPreview(IReadOnlyList<OptimizationFile> Files, IReadOnlyList<OptimizationSkipped> Skipped,
    long EstimatedBytes, bool UsesLogicalSize, bool Limited);
public enum OptimizationOutcome { Deleted, Skipped, Failed }
public sealed record OptimizationFileResult(string Path, OptimizationOutcome Outcome, long LogicalBytes, string Message);
public sealed record OptimizationResult(IReadOnlyList<OptimizationFileResult> Files, bool Cancelled, string? Note = null)
{
    public int DeletedCount => Files.Count(f => f.Outcome == OptimizationOutcome.Deleted);
    // Removed file lengths, not a claim about free drive space.
    public long RemovedLogicalBytes => Files.Where(f => f.Outcome == OptimizationOutcome.Deleted).Sum(f => f.LogicalBytes);
}

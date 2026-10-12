using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SpaceMaid.Core.Analysis;

public enum FileType
{
    Unknown, NoExtension, Image, RawPhoto, Video, Audio, SourceOrConfiguration,
    Document, ArchiveOrInstaller, Binary, Database, DiskImage, LogOrTemporary,
    Ambiguous, StructuredData, GameData, Model3D, Engineering, Electronics,
    ScientificData, BiomedicalData, Geospatial, MachineLearning, Communication,
    Security, Virtualization, Business, Education, Font, SystemMetadata, Other
}

public enum FileFormatStatus { Unknown, Known, Ambiguous }
public enum FileNameMatchKind { None, ExactName, Suffix, AppendedSuffix }
public sealed record FileFormatInfo(string Id, string Name, string CategoryId);
public sealed record FileFormatComposition(FileFormatInfo Inner, FileFormatInfo Outer);

// Known means one interpretation in the supplied filename evidence, never
// verified contents, validity, codec support or permission to delete.
public readonly record struct FileFormatMatch(string FileName, string MatchedValue, FileNameMatchKind MatchKind,
    FileType Type, FileType? CandidateCategory, FileFormatStatus Status, IReadOnlyList<FileFormatInfo> Candidates,
    FileFormatComposition? Composition, string? Note, string? CaseConvention,
    bool HasUnjoinedEvidence, bool ForbidsExtensionOnly)
{
    public string Method => "filename";
    public bool ContentVerified => false;
    public int InterpretationCount => Candidates.Count + (Composition is null ? 0 : 1);
}

// One embedded registry shared by displayed rows. Lookups use bounded stack
// storage and ordinal span keys; scanning never classifies or opens every file.
public static class FileTypeClassifier
{
    private static readonly Lazy<Registry> Data = new(Load);
    public static int ExtensionCount => Data.Value.Extensions.Count;
    public static int FormatCount => Data.Value.Formats.Count;
    public static void Prepare() => _ = Data.Value;

    public static FileType Classify(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Find(Path.GetFileName(fileName.AsSpan())).Type;
    }

    public static FileType Classify(ReadOnlySpan<char> fileName) => Find(Path.GetFileName(fileName)).Type;

    public static FileFormatMatch Infer(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string name = Path.GetFileName(fileName);
        var found = Find(name.AsSpan());
        var entry = found.Entry;
        return new(name, entry?.Key ?? UnmatchedSuffix(name), found.Kind, found.Type, entry?.CandidateCategory,
            entry?.Status ?? FileFormatStatus.Unknown, entry?.Candidates ?? [], entry?.Composition,
            entry?.Note, found.CaseConvention, entry?.HasUnjoinedEvidence ?? false, entry?.ForbidsExtensionOnly ?? false);
    }

    public static string Describe(string fileName)
    {
        var match = Infer(fileName);
        string text = "Inferred type: " + Label(match.Type) + (match.MatchedValue.Length > 0 ? $" ({match.MatchedValue})" : "");
        if (match.Type == FileType.Ambiguous && match.CandidateCategory is { } category)
            text += " · Candidate category: " + Label(category);
        if (match.Status == FileFormatStatus.Ambiguous)
            text += " · " + (match.Type == FileType.Ambiguous ? "" : "Ambiguous: ")
                + $"{match.InterpretationCount:N0} {(match.InterpretationCount == 1 ? "candidate + unresolved hints" : "interpretations")}";
        else if (match.Status == FileFormatStatus.Unknown && match.MatchKind != FileNameMatchKind.None)
            text += " · Format unknown";
        return text;
    }

    public static string DescribeDetails(string fileName)
    {
        var match = Infer(fileName);
        var text = new StringBuilder(Describe(fileName));
        if (match.Composition is { } composition)
            text.Append("\nComposition: ").Append(composition.Inner.Name).Append(" wrapped in ").Append(composition.Outer.Name);
        if (match.Candidates.Count > 0)
        {
            // Alphabetical presentation is not a preference or confidence rank.
            var names = match.Candidates.Select(candidate => candidate.Name).Order(StringComparer.Ordinal).Take(8);
            text.Append("\nCandidates: ").AppendJoin("; ", names);
            if (match.Candidates.Count > 8) text.Append($"; +{match.Candidates.Count - 8:N0} more");
        }
        if (match.Note is { } note) text.Append('\n').Append(note);
        if (match.CaseConvention is { } convention) text.Append("\nCase convention: ").Append(convention).Append("; independent alternatives retained.");
        if (match.HasUnjoinedEvidence) text.Append("\nSome source evidence is absent from the supplied tables.");
        if (match.ForbidsExtensionOnly) text.Append("\nAn upstream source warns against identification from the extension alone.");
        text.Append("\nFilename evidence only; contents are not verified. Type does not establish cleanup safety.");
        return text.ToString();
    }

    private static Found Find(ReadOnlySpan<char> name)
    {
        var data = Data.Value;
        Span<char> folded = stackalloc char[data.MaxKeyLength];
        if (name.Length <= folded.Length && data.ExactNames.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(Fold(name, folded), out var exact))
            return new(exact, FileNameMatchKind.ExactName, exact.Type, null);
        var suffixes = data.Extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        // Only endings actually present in the registry are recognized. Skip
        // a leading dot; it belongs to an otherwise extensionless basename.
        for (int index = 1; index < name.Length; index++)
        {
            if (name[index] != '.' || name.Length - index > folded.Length) continue;
            var suffix = name[index..];
            if (!suffixes.TryGetValue(Fold(suffix, folded), out var entry)) continue;
            string? convention = null;
            foreach (var rule in entry.CaseRules)
                if (suffix.SequenceEqual(rule.Spelling)) { convention = rule.Format.Name; break; }
            return new(entry, FileNameMatchKind.Suffix, entry.Type, convention);
        }
        foreach (var appended in data.Appended)
            if (name.Length > appended.Key.Length && Fold(name[^appended.Key.Length..], folded).SequenceEqual(appended.Key))
                return new(appended, FileNameMatchKind.AppendedSuffix, appended.Type, null);
        int dot = name.LastIndexOf('.');
        return new(null, FileNameMatchKind.None, dot <= 0 || dot == name.Length - 1 ? FileType.NoExtension : FileType.Unknown, null);
    }

    private static ReadOnlySpan<char> Fold(ReadOnlySpan<char> input, Span<char> buffer)
    {
        for (int i = 0; i < input.Length; i++) buffer[i] = input[i] is >= 'A' and <= 'Z' ? (char)(input[i] + 32) : input[i];
        return buffer[..input.Length];
    }
    private static string UnmatchedSuffix(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..] : "";
    }

    private static Registry Load()
    {
        using var resource = typeof(FileTypeClassifier).Assembly.GetManifestResourceStream("SpaceMaid.FileFormatRegistry")
            ?? throw new InvalidDataException("The built-in file-format registry is missing.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var source = JsonSerializer.Deserialize<RegistryDefinition>(gzip) ?? throw new InvalidDataException("The file-format registry is empty.");
        if (source.SchemaVersion != 1 || source.Extensions.Length != source.ExtensionCount || source.Formats.Length != source.FormatCount)
            throw new InvalidDataException("The file-format registry schema or counts are inconsistent.");
        var formats = source.Formats.Select(format => new FileFormatInfo(format.Id, format.Name, format.CategoryId)).ToArray();
        var byId = formats.ToFrozenDictionary(format => format.Id, StringComparer.Ordinal);
        var extensions = source.Extensions.Select(row => CreateEntry(row, formats)).ToFrozenDictionary(entry => entry.Key, StringComparer.Ordinal);
        // The supplied CSVs omit filename_rules. These small rules are the
        // report's documented examples, not an invented import of all 522 rules.
        var names = new Dictionary<string, Entry>(StringComparer.Ordinal)
        {
            ["dockerfile"] = Exact("Dockerfile", byId["fmt:dockerfile"], FileType.SourceOrConfiguration),
            ["makefile"] = Exact("Makefile", byId["fmt:makefile"], FileType.SourceOrConfiguration),
            ["gnumakefile"] = Exact("GNUmakefile", byId["fmt:makefile"], FileType.SourceOrConfiguration),
            [".gitignore"] = Exact(".gitignore", byId["fmt:gitignore"], FileType.SourceOrConfiguration),
            [".env"] = Hint(".env", FileType.SourceOrConfiguration, "dotenv-style filename convention; format remains unknown."),
            [".env.local"] = Hint(".env.local", FileType.SourceOrConfiguration, "dotenv-style filename convention; format remains unknown."),
            ["license"] = Hint("LICENSE", FileType.Document, "License filename convention; format remains unknown.")
        }.ToFrozenDictionary(StringComparer.Ordinal);
        Entry[] appended = [Exact("-journal", byId["fmt:sqlite_journal"], FileType.Database),
            Exact("-wal", byId["fmt:sqlite_wal"], FileType.Database), Exact("-shm", byId["fmt:sqlite_shm"], FileType.Database)];
        int maxKeyLength = Math.Max(extensions.Keys.Max(key => key.Length), names.Keys.Max(key => key.Length));
        return new(extensions, byId, names, appended, maxKeyLength);
    }

    private static Entry CreateEntry(ExtensionDefinition row, FileFormatInfo[] formats)
    {
        var candidates = Array.AsReadOnly(row.CandidateIndexes.Select(index => formats[index]).ToArray());
        var composition = row.InnerIndexes.Length == 1 && row.OuterIndexes.Length == 1
            ? new FileFormatComposition(formats[row.InnerIndexes[0]], formats[row.OuterIndexes[0]]) : null;
        int count = candidates.Count + (composition is null ? 0 : 1);
        bool incompleteComposition = composition is null && (row.InnerIndexes.Length > 0 || row.OuterIndexes.Length > 0);
        var status = count == 0 ? FileFormatStatus.Unknown
            : count > 1 || row.HasClassificationHints || incompleteComposition ? FileFormatStatus.Ambiguous : FileFormatStatus.Known;
        var interpretations = candidates.Select(candidate => Category(candidate.CategoryId)).ToList();
        if (composition is not null) interpretations.Add(Category(composition.Inner.CategoryId));
        FileType type = interpretations.Count == 0 ? FileType.Unknown
            : !row.HasClassificationHints && !incompleteComposition && interpretations.All(item => item == interpretations[0]) ? interpretations[0] : FileType.Ambiguous;
        // Broaden differing image subcategories without assuming that every
        // image interpretation is a camera RAW structure.
        FileType? candidateCategory = interpretations.Count > 0 && interpretations.All(item => item == interpretations[0]) ? interpretations[0] : null;
        if (interpretations.Count > 0 && interpretations.All(item => item is FileType.Image or FileType.RawPhoto)
            && interpretations.Any(item => item == FileType.Image)) candidateCategory = FileType.Image;
        if (!row.HasClassificationHints && !incompleteComposition && candidateCategory is { } common) type = common;
        string? note = row.HasClassificationHints ? "Additional classification hints were not supplied; their meaning remains unresolved." : null;
        if (row.Conventions.Length > 0) note = JoinNote(note, "Naming convention: " + string.Join(", ", row.Conventions.Select(value => value.Replace('_', ' '))) + ".");
        if (incompleteComposition) note = JoinNote(note, "Composition evidence is incomplete; no wrapper/payload combination was guessed.");
        return new(row.Suffix, candidates, composition, status, type, candidateCategory, note, row.HasUnjoinedEvidence, row.ForbidsExtensionOnly,
            row.CaseRules.Select(rule => new CaseRule(rule.Spelling, formats[rule.FormatIndex])).ToArray());
    }

    private static string JoinNote(string? first, string second) => first is null ? second : first + " " + second;
    private static Entry Exact(string key, FileFormatInfo format, FileType type) => new(key, Array.AsReadOnly(new[] { format }), null,
        FileFormatStatus.Known, type, type, null, false, false, []);
    private static Entry Hint(string key, FileType type, string note) => new(key, [], null, FileFormatStatus.Unknown, type, null, note, false, false, []);

    private static FileType Category(string category) => category switch
    {
        "image.camera_raw" => FileType.RawPhoto,
        "code.object" or "code.bytecode" => FileType.Binary,
        "storage.disk_image" or "virtualization.disk" => FileType.DiskImage,
        "data.configuration" => FileType.SourceOrConfiguration,
        "executable.package" or "executable.installer" => FileType.ArchiveOrInstaller,
        _ => category.Split('.')[0] switch
        {
            "image" => FileType.Image, "video" => FileType.Video, "audio" => FileType.Audio,
            "code" => FileType.SourceOrConfiguration, "document" => FileType.Document,
            "archive" => FileType.ArchiveOrInstaller, "executable" => FileType.Binary,
            "database" => FileType.Database, "storage" => FileType.DiskImage, "diagnostic" => FileType.LogOrTemporary,
            "data" => FileType.StructuredData, "game" => FileType.GameData, "model3d" => FileType.Model3D,
            "engineering" => FileType.Engineering, "electronics" => FileType.Electronics,
            "science" => FileType.ScientificData, "biomed" => FileType.BiomedicalData,
            "geospatial" => FileType.Geospatial, "ai" => FileType.MachineLearning, "communication" => FileType.Communication,
            "security" => FileType.Security, "virtualization" => FileType.Virtualization, "business" => FileType.Business,
            "education" => FileType.Education, "font" => FileType.Font, "system" => FileType.SystemMetadata, _ => FileType.Other
        }
    };

    public static string Label(FileType type) => type switch
    {
        FileType.NoExtension => "No extension", FileType.Image => "Image", FileType.RawPhoto => "RAW photo",
        FileType.Video => "Video", FileType.Audio => "Audio", FileType.SourceOrConfiguration => "Source / configuration",
        FileType.Document => "Document", FileType.ArchiveOrInstaller => "Archive / installer",
        FileType.Binary => "Binary / executable / library", FileType.Database => "Database", FileType.DiskImage => "Disk image",
        FileType.LogOrTemporary => "Diagnostic / event record", FileType.Ambiguous => "Ambiguous",
        FileType.StructuredData => "Structured data / metadata", FileType.GameData => "Game / interactive media",
        FileType.Model3D => "3D model / graphics", FileType.Engineering => "Engineering / manufacturing",
        FileType.Electronics => "Electronics / circuit design", FileType.ScientificData => "Scientific / numerical data",
        FileType.BiomedicalData => "Biology / medical data", FileType.Geospatial => "Geospatial / navigation",
        FileType.MachineLearning => "AI / machine learning", FileType.Communication => "Communication / network data",
        FileType.Security => "Security / cryptography", FileType.Virtualization => "Virtualization / infrastructure",
        FileType.Business => "Business / administration", FileType.Education => "Education / language data",
        FileType.Font => "Font / typography", FileType.SystemMetadata => "Operating-system metadata",
        FileType.Other => "Other documented format", _ => "Unknown"
    };

    private readonly record struct Found(Entry? Entry, FileNameMatchKind Kind, FileType Type, string? CaseConvention);
    private sealed record CaseRule(string Spelling, FileFormatInfo Format);
    private sealed record Entry(string Key, IReadOnlyList<FileFormatInfo> Candidates, FileFormatComposition? Composition,
        FileFormatStatus Status, FileType Type, FileType? CandidateCategory, string? Note, bool HasUnjoinedEvidence, bool ForbidsExtensionOnly, CaseRule[] CaseRules);
    private sealed record Registry(FrozenDictionary<string, Entry> Extensions, FrozenDictionary<string, FileFormatInfo> Formats,
        FrozenDictionary<string, Entry> ExactNames, Entry[] Appended, int MaxKeyLength);
    private sealed record RegistryDefinition(int SchemaVersion, int ExtensionCount, int FormatCount, int AssociationCount,
        FormatDefinition[] Formats, ExtensionDefinition[] Extensions);
    private sealed record FormatDefinition(string Id, string Name, string CategoryId);
    private sealed record ExtensionDefinition(string Suffix, int[] CandidateIndexes, int[] InnerIndexes, int[] OuterIndexes,
        bool HasClassificationHints, bool HasUnjoinedEvidence, bool ForbidsExtensionOnly, string[] Conventions, CaseDefinition[] CaseRules);
    private sealed record CaseDefinition(string Spelling, int FormatIndex);
}

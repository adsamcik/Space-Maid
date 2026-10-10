namespace SpaceMaid.Core.Analysis;

public enum FileType
{
    Unknown,
    NoExtension,
    Image,
    RawPhoto,
    Video,
    Audio,
    SourceOrConfiguration,
    Document,
    ArchiveOrInstaller,
    Binary,
    Database,
    DiskImage,
    LogOrTemporary
}

// Filename inference only. Callers classify displayed files on demand; scanning
// does not read content, create per-file classifications, or change cleanup rules.
public static class FileTypeClassifier
{
    public static FileType Classify(string fileName) => ClassifyExtension(Extension(fileName));

    public static string Describe(string fileName)
    {
        string extension = Extension(fileName);
        string label = ClassifyExtension(extension) switch
        {
            FileType.NoExtension => "No extension",
            FileType.Image => "Image",
            FileType.RawPhoto => "RAW photo",
            FileType.Video => "Video",
            FileType.Audio => "Audio",
            FileType.SourceOrConfiguration => "Source / configuration",
            FileType.Document => "Document",
            FileType.ArchiveOrInstaller => "Archive / installer",
            FileType.Binary => "Binary / executable / library",
            FileType.Database => "Database",
            FileType.DiskImage => "Disk image",
            FileType.LogOrTemporary => "Log / temporary",
            _ => "Unknown"
        };
        return "Inferred type: " + label + (extension.Length > 0 ? $" ({extension})" : "");
    }

    private static string Extension(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant();
    }

    private static FileType ClassifyExtension(string extension) => extension switch
    {
        "" => FileType.NoExtension,
        ".jpg" or ".jpeg" or ".jpe" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or
        ".webp" or ".avif" or ".heic" or ".heif" or ".jxl" or ".svg" or ".ico" or
        ".psd" or ".psb" => FileType.Image,
        ".dng" or ".cr2" or ".cr3" or ".crw" or ".nef" or ".nrw" or ".arw" or ".srf" or
        ".sr2" or ".raf" or ".rw2" or ".orf" or ".pef" or ".srw" or ".x3f" or
        ".3fr" or ".fff" or ".iiq" or ".rwl" => FileType.RawPhoto,
        ".mp4" or ".m4v" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" or
        ".mpeg" or ".mpg" or ".m2ts" or ".mts" or ".vob" or ".flv" or ".3gp" => FileType.Video,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" or
        ".wma" or ".aiff" or ".aif" or ".alac" or ".mid" or ".midi" => FileType.Audio,
        ".cs" or ".csx" or ".fs" or ".fsx" or ".vb" or ".c" or ".h" or ".cpp" or ".hpp" or
        ".cc" or ".cxx" or ".rs" or ".go" or ".java" or ".kt" or ".kts" or ".py" or
        ".pyi" or ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".swift" or
        ".dart" or ".rb" or ".php" or ".lua" or ".r" or ".sql" or ".ps1" or ".psm1" or
        ".psd1" or ".sh" or ".bat" or ".cmd" or ".html" or ".htm" or ".css" or ".scss" or
        ".sass" or ".less" or ".vue" or ".svelte" or ".json" or ".jsonc" or ".jsonl" or
        ".xml" or ".xaml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or
        ".config" or ".env" or ".gitignore" or ".gitattributes" or ".gitmodules" or
        ".editorconfig" or ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx" or
        ".props" or ".targets" or ".gradle" or ".ipynb" => FileType.SourceOrConfiguration,
        ".pdf" or ".txt" or ".md" or ".markdown" or ".rst" or ".rtf" or ".doc" or ".docx" or
        ".odt" or ".xls" or ".xlsx" or ".ods" or ".csv" or ".tsv" or ".ppt" or ".pptx" or
        ".odp" or ".epub" or ".mobi" or ".tex" => FileType.Document,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" or
        ".tgz" or ".tbz2" or ".txz" or ".cab" or ".msi" or ".msix" or ".msixbundle" or
        ".appx" or ".appxbundle" or ".nupkg" or ".snupkg" or ".jar" or ".war" or ".apk" or
        ".aab" or ".deb" or ".rpm" or ".pak" => FileType.ArchiveOrInstaller,
        ".exe" or ".dll" or ".sys" or ".com" or ".ocx" or ".so" or ".dylib" or ".lib" or
        ".a" or ".o" or ".obj" or ".pdb" or ".class" or ".pyc" or ".wasm" or ".bin" => FileType.Binary,
        ".db" or ".db3" or ".sqlite" or ".sqlite3" or ".mdb" or ".accdb" or ".mdf" or
        ".ldf" or ".ndf" or ".duckdb" => FileType.Database,
        ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".vdi" or ".qcow" or
        ".qcow2" or ".dmg" => FileType.DiskImage,
        ".log" or ".tmp" or ".temp" or ".bak" => FileType.LogOrTemporary,
        _ => FileType.Unknown
    };
}

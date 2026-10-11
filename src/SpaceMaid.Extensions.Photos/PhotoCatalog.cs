using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.Extensions.Photos;

internal enum PhotoMode { Browse, Duplicates, Families }
internal enum PhotoSort { Name, Size, Type, Captured, Status, Group }
internal sealed record PhotoCollection(long Id, string Root, string Name, string Status, string? Observed, long Count, string? Coverage = null);
internal sealed record PhotoRow(string ObjectId, string Path, string Name, string Role, long LogicalBytes, long? AllocatedBytes,
    long? DefaultBytes, string Revision, bool ContentAvailable, uint Links, string Status, string? Format,
    int? Width, int? Height, int? Orientation, string? Captured, string? Hash, string? Verified, string SidecarRevision,
    string? GroupLabel, long GroupCount)
{
    public bool AnalysisCurrent { get; init; }
    public string SizeText => PhotoFormatting.Bytes(DefaultBytes ?? LogicalBytes);
    public string TypeText => Format ?? Role;
    public string GroupText => Hash is { Length: >= 12 } && GroupCount > 1 ? $"{Hash[..12]} · {GroupCount:N0} files" : GroupLabel?.StartsWith("Possible", StringComparison.Ordinal) == true ? PhotoKinds.Stem(Path) : "";
    public string Detail => $"{Path}\n\n{Role} · {TypeText}\nDefault stream: {(DefaultBytes is long n ? PhotoFormatting.Bytes(n) : "Unverified")}\nObserved logical size: {PhotoFormatting.Bytes(LogicalBytes)}\nAttributed allocation: {(AllocatedBytes is long a ? PhotoFormatting.Bytes(a) : "Unknown")}\nNative hard links: {(Links > 0 ? Links.ToString() : "Unknown")}\n{(Width is int w && Height is int h ? $"Dimensions: {w} × {h}\n" : "")}Orientation: {Orientation?.ToString() ?? "Unknown"}\nCapture time: {Captured ?? "Unknown"}\n{Status}\nLast content verification: {Verified ?? "Never"}\n{GroupLabel}";
}
internal sealed record PhotoPage(IReadOnlyList<PhotoRow> Rows, long Total, int Page);
internal sealed record PhotoObservation(string Path, string Identity, string Revision, long? DefaultBytes,
    long LogicalBytes, long? AllocatedBytes, uint Links, bool ContentAvailable, string Status, string Role, string Stem);

internal static class PhotoKinds
{
    private static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".ptx", ".rwl", ".srw", ".3fr", ".fff", ".iiq", ".kdc", ".dcr", ".mrw", ".mos", ".erf" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".png", ".tif", ".tiff", ".gif", ".bmp", ".webp", ".heic", ".heif", ".avif", ".jxl", ".ico" };
    public static string? Role(string path)
    {
        var extension = Path.GetExtension(path);
        return Raw.Contains(extension) ? "RAW source" : Images.Contains(extension) ? "Viewing image" :
            extension.Equals(".xmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".acr", StringComparison.OrdinalIgnoreCase) ? "Edit sidecar" : null;
    }
    public static string Stem(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (Role(path) == "Edit sidecar" && Raw.Contains(Path.GetExtension(name))) name = Path.GetFileNameWithoutExtension(name);
        return name.ToUpperInvariant();
    }
}

internal sealed class PhotoCatalog : IDisposable
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    public PhotoCatalog(string directory)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Join(directory, "photos.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var timeout = connection.CreateCommand(); timeout.CommandText = "PRAGMA busy_timeout=5000;"; timeout.ExecuteNonQuery();
        return connection;
    }
    public string Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) > 1) throw new InvalidOperationException("The Photos catalog was created by a newer extension.");
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS collections(id INTEGER PRIMARY KEY, root TEXT NOT NULL UNIQUE COLLATE BINARY,
                    name TEXT NOT NULL, status TEXT NOT NULL, observed TEXT, generation TEXT, coverage TEXT);
                CREATE TABLE IF NOT EXISTS objects(id TEXT PRIMARY KEY, revision TEXT NOT NULL, default_bytes INTEGER,
                    logical_bytes INTEGER NOT NULL, allocated_bytes INTEGER, links INTEGER NOT NULL,
                    available INTEGER NOT NULL, status TEXT NOT NULL, allocation_generation TEXT, format TEXT, width INTEGER, height INTEGER,
                    orientation INTEGER, captured TEXT, metadata_revision TEXT, tool_version TEXT,
                    hash TEXT, hash_revision TEXT, content_revision TEXT, verified TEXT);
                CREATE TABLE IF NOT EXISTS locations(collection_id INTEGER NOT NULL REFERENCES collections(id), path TEXT NOT NULL COLLATE BINARY,
                    object_id TEXT NOT NULL REFERENCES objects(id), name TEXT NOT NULL, parent TEXT NOT NULL COLLATE BINARY,
                    role TEXT NOT NULL, stem TEXT NOT NULL, generation TEXT NOT NULL, missing INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(collection_id,path));
                CREATE INDEX IF NOT EXISTS location_objects ON locations(collection_id,missing,object_id);
                CREATE INDEX IF NOT EXISTS location_members ON locations(object_id,collection_id,missing,path);
                CREATE INDEX IF NOT EXISTS location_names ON locations(collection_id,missing,name COLLATE NOCASE,path);
                CREATE INDEX IF NOT EXISTS location_families ON locations(collection_id,missing,parent,stem);
                CREATE INDEX IF NOT EXISTS object_hashes ON objects(hash,default_bytes,hash_revision);
                CREATE INDEX IF NOT EXISTS object_verified_sizes ON objects(default_bytes,id)
                    WHERE available=1 AND content_revision=revision AND default_bytes IS NOT NULL;
                CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, collection_id INTEGER NOT NULL REFERENCES collections(id),
                    kind TEXT NOT NULL, state TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0,
                    started TEXT NOT NULL, updated TEXT NOT NULL, message TEXT);
                UPDATE jobs SET state='Interrupted', message='Interrupted work must be revalidated before resuming.' WHERE state='Running';
                UPDATE collections SET status='Interrupted discovery · Refresh to validate coverage' WHERE status='Discovering';
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
            using var nativeVersion = connection.CreateCommand(); nativeVersion.CommandText = "SELECT sqlite_version()";
            return Convert.ToString(nativeVersion.ExecuteScalar())!;
        }
    }
    public long AddCollection(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO collections(root,name,status) VALUES($root,$name,'Not discovered') ON CONFLICT(root) DO NOTHING; SELECT id FROM collections WHERE root=$root;";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$name", Path.GetFileName(root) is { Length: > 0 } name ? name : root);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
    public IReadOnlyList<PhotoCollection> Collections()
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.id,c.root,c.name,c.status,c.observed,COUNT(l.path),c.coverage
                FROM collections c LEFT JOIN locations l ON l.collection_id=c.id AND l.missing=0
                GROUP BY c.id ORDER BY c.name COLLATE NOCASE,c.root;
                """;
            using var reader = command.ExecuteReader(); var result = new List<PhotoCollection>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), String(reader, 4), reader.GetInt64(5), String(reader, 6)));
            return result;
        }
    }
    public void BeginDiscovery(long collection, string generation)
    {
        // Invalidate the entire selected scope before any asynchronous reads.
        // Otherwise an interrupted discovery could leave untouched cached rows
        // authorized for source previews or verified duplicate review.
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                UPDATE objects SET content_revision=NULL,hash_revision=NULL,metadata_revision=NULL,
                    status='Earlier observation · Refresh incomplete; Analyze to revalidate content'
                    WHERE id IN (SELECT object_id FROM locations WHERE collection_id=$collection AND missing=0);
                UPDATE collections SET status='Discovering',generation=$generation WHERE id=$collection;
                """;
            Add(command, "$collection", collection); Add(command, "$generation", generation);
            command.ExecuteNonQuery(); transaction.Commit();
        }
    }
    public void Observe(long collection, string generation, IReadOnlyList<PhotoObservation> observations)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO objects(id,revision,default_bytes,logical_bytes,allocated_bytes,links,available,status,allocation_generation)
                    VALUES($id,$revision,$default,$logical,$allocated,$links,$available,$status,$generation)
                ON CONFLICT(id) DO UPDATE SET revision=$revision,default_bytes=$default,logical_bytes=$logical,
                    allocated_bytes=CASE WHEN objects.allocation_generation=$generation THEN
                        CASE WHEN objects.allocated_bytes IS NULL THEN $allocated WHEN $allocated IS NULL THEN objects.allocated_bytes ELSE MAX(objects.allocated_bytes,$allocated) END ELSE $allocated END,
                    allocation_generation=$generation,links=$links,available=$available,status=$status,hash_revision=NULL,metadata_revision=NULL,content_revision=NULL;
                INSERT INTO locations(collection_id,path,object_id,name,parent,role,stem,generation,missing)
                    VALUES($collection,$path,$id,$name,$parent,$role,$stem,$generation,0)
                ON CONFLICT(collection_id,path) DO UPDATE SET object_id=$id,name=$name,parent=$parent,
                    role=$role,stem=$stem,generation=$generation,missing=0;
                """;
            // Reuse the compiled statements and typed parameter objects for this
            // bounded transaction. Only observation values change between rows.
            var id = Parameter("$id", SqliteType.Text);
            var revision = Parameter("$revision", SqliteType.Text);
            var defaultBytes = Parameter("$default", SqliteType.Integer);
            var logical = Parameter("$logical", SqliteType.Integer);
            var allocated = Parameter("$allocated", SqliteType.Integer);
            var links = Parameter("$links", SqliteType.Integer);
            var available = Parameter("$available", SqliteType.Integer);
            var status = Parameter("$status", SqliteType.Text);
            Parameter("$collection", SqliteType.Integer).Value = collection;
            var path = Parameter("$path", SqliteType.Text);
            var name = Parameter("$name", SqliteType.Text);
            var parent = Parameter("$parent", SqliteType.Text);
            var role = Parameter("$role", SqliteType.Text);
            var stem = Parameter("$stem", SqliteType.Text);
            Parameter("$generation", SqliteType.Text).Value = (object?)generation ?? DBNull.Value;
            if (observations.Count > 0) command.Prepare();
            foreach (var observation in observations)
            {
                id.Value = (object?)observation.Identity ?? DBNull.Value;
                revision.Value = (object?)observation.Revision ?? DBNull.Value;
                defaultBytes.Value = (object?)observation.DefaultBytes ?? DBNull.Value;
                logical.Value = observation.LogicalBytes;
                allocated.Value = (object?)observation.AllocatedBytes ?? DBNull.Value;
                links.Value = (long)observation.Links;
                available.Value = observation.ContentAvailable ? 1 : 0;
                status.Value = (object?)observation.Status ?? DBNull.Value;
                path.Value = (object?)observation.Path ?? DBNull.Value;
                name.Value = (object?)Path.GetFileName(observation.Path) ?? DBNull.Value;
                parent.Value = (object?)Path.GetDirectoryName(observation.Path) ?? DBNull.Value;
                role.Value = (object?)observation.Role ?? DBNull.Value;
                stem.Value = (object?)observation.Stem ?? DBNull.Value;
                command.ExecuteNonQuery();
            }
            transaction.Commit();

            SqliteParameter Parameter(string parameterName, SqliteType type)
            {
                var parameter = command.Parameters.Add(parameterName, type);
                parameter.Value = DBNull.Value;
                return parameter;
            }
        }
    }
    public void EndDiscovery(long collection, string generation, bool complete, string status, string? coverage = null)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            if (complete)
            {
                command.CommandText = "UPDATE locations SET missing=1 WHERE collection_id=$collection AND generation<>$generation;";
                Add(command, "$collection", collection); Add(command, "$generation", generation); command.ExecuteNonQuery(); command.Parameters.Clear();
            }
            command.CommandText = "UPDATE collections SET status=$status,observed=$observed,coverage=$coverage WHERE id=$collection";
            Add(command, "$status", status); Add(command, "$coverage", coverage); Add(command, "$observed", DateTime.UtcNow.ToString("O")); Add(command, "$collection", collection);
            command.ExecuteNonQuery(); transaction.Commit();
        }
    }
    public PhotoPage Page(long collection, PhotoMode mode, PhotoSort sort, bool descending, int page)
    {
        const string current = "l.collection_id=$collection AND l.missing=0";
        var modeFilter = mode switch
        {
            PhotoMode.Duplicates => " AND o.hash IS NOT NULL AND o.hash_revision=o.revision AND EXISTS(SELECT 1 FROM locations x JOIN objects p ON p.id=x.object_id WHERE x.collection_id=l.collection_id AND x.missing=0 AND p.id<>o.id AND p.hash=o.hash AND p.hash_revision=p.revision AND p.default_bytes=o.default_bytes)",
            PhotoMode.Families => " AND EXISTS(SELECT 1 FROM locations x WHERE x.collection_id=l.collection_id AND x.missing=0 AND x.parent=l.parent AND x.stem=l.stem AND x.object_id<>l.object_id AND x.role<>l.role)",
            _ => ""
        };
        var column = sort switch { PhotoSort.Size => "COALESCE(o.default_bytes,o.logical_bytes)", PhotoSort.Type => "COALESCE(o.format,l.role)", PhotoSort.Captured => "o.captured", PhotoSort.Status => "o.status", PhotoSort.Group => mode == PhotoMode.Duplicates ? "o.hash" : "l.stem", _ => "l.name COLLATE NOCASE" };
        lock (_gate)
        {
            using var connection = Open();
            using var count = connection.CreateCommand(); count.CommandText = $"SELECT COUNT(*) FROM locations l JOIN objects o ON o.id=l.object_id WHERE {current}{modeFilter}";
            Add(count, "$collection", collection); var total = Convert.ToInt64(count.ExecuteScalar());
            page = Math.Clamp(page, 0, (int)Math.Min(int.MaxValue, Math.Max(0, (total - 1) / 100)));
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT o.id,l.path,l.name,l.role,o.logical_bytes,o.allocated_bytes,o.default_bytes,o.revision,o.available,
                    o.links,o.status,o.format,o.width,o.height,o.orientation,o.captured,
                    CASE WHEN o.hash_revision=o.revision THEN o.hash END,o.verified,
                    COALESCE((SELECT GROUP_CONCAT(p.revision,'|') FROM locations s JOIN objects p ON p.id=s.object_id WHERE s.collection_id=l.collection_id AND s.missing=0 AND s.parent=l.parent AND s.stem=l.stem AND s.role='Edit sidecar'),''),
                    (SELECT COUNT(DISTINCT p.id) FROM locations x JOIN objects p ON p.id=x.object_id WHERE x.collection_id=l.collection_id AND x.missing=0 AND p.hash=o.hash AND p.hash_revision=p.revision AND p.default_bytes=o.default_bytes),
                    COALESCE(o.content_revision=o.revision,0)
                FROM locations l JOIN objects o ON o.id=l.object_id WHERE {current}{modeFilter}
                ORDER BY {column} {(descending ? "DESC" : "ASC")},l.path COLLATE BINARY,o.id LIMIT 100 OFFSET $offset;
                """;
            Add(command, "$collection", collection); Add(command, "$offset", page * 100L);
            using var reader = command.ExecuteReader(); var rows = new List<PhotoRow>(100);
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), Long(reader, 5), Long(reader, 6), reader.GetString(7), reader.GetBoolean(8), (uint)reader.GetInt64(9), reader.GetString(10), String(reader, 11), Integer(reader, 12), Integer(reader, 13), Integer(reader, 14), String(reader, 15), String(reader, 16), String(reader, 17), reader.GetString(18),
                mode == PhotoMode.Duplicates ? "Equal default streams · Alternate streams and companion files are outside this comparison." : mode == PhotoMode.Families ? "Possible relationship · Matching basename and folder; does not establish edit fidelity or equivalence." : null, reader.GetInt64(19)) { AnalysisCurrent = reader.GetBoolean(20) });
            return new(rows, total, page);
        }
    }
    // One representative path per physical object. Keyset paging avoids a collection-sized object list.
    internal static string AnalysisQuery(bool candidatesOnly) => $"""
        SELECT o.id,
            (SELECT MIN(l.path) FROM locations l WHERE l.object_id=o.id AND l.collection_id=$collection AND l.missing=0),
            o.revision,o.logical_bytes,o.allocated_bytes,o.default_bytes,o.links,o.available,o.status,
            (SELECT l.role FROM locations l WHERE l.object_id=o.id AND l.collection_id=$collection AND l.missing=0 ORDER BY l.path COLLATE BINARY LIMIT 1)
        FROM objects o WHERE o.id>$after
            AND EXISTS(SELECT 1 FROM locations l WHERE l.object_id=o.id AND l.collection_id=$collection AND l.missing=0)
            {(candidatesOnly ? """
                AND o.available=1 AND o.default_bytes IS NOT NULL AND o.content_revision=o.revision
                AND EXISTS(SELECT 1 FROM objects p INDEXED BY object_verified_sizes
                    WHERE p.default_bytes=o.default_bytes AND p.id<>o.id AND p.available=1
                        AND p.content_revision=p.revision AND p.default_bytes IS NOT NULL
                        AND EXISTS(SELECT 1 FROM locations x WHERE x.object_id=p.id AND x.collection_id=$collection AND x.missing=0))
                """ : "")}
        ORDER BY o.id LIMIT 64;
        """;
    public IReadOnlyList<PhotoRow> AnalysisBatch(long collection, string? after, bool candidatesOnly = false, CancellationToken token = default)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = AnalysisQuery(candidatesOnly);
            Add(command, "$collection", collection); Add(command, "$after", after ?? "");
            // SqliteCommand.Cancel is documented as a no-op. Use SQLite's
            // interruption API while this dedicated connection is alive.
            using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            token.ThrowIfCancellationRequested();
            try
            {
                using var reader = command.ExecuteReader(); var rows = new List<PhotoRow>(64);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(new(reader.GetString(0), reader.GetString(1), Path.GetFileName(reader.GetString(1)), reader.GetString(9), reader.GetInt64(3), Long(reader, 4), Long(reader, 5), reader.GetString(2), reader.GetBoolean(7), (uint)reader.GetInt64(6), reader.GetString(8), null, null, null, null, null, null, null, "", null, 0));
                }
                token.ThrowIfCancellationRequested(); return rows;
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
    }
    public void StoreAnalysis(string id, MediaRevision revision, MediaToolMetadata? metadata, string? hash, string status)
    {
        Execute("""
            UPDATE objects SET revision=$revision,default_bytes=$length,links=$links,available=$available,status=$status,
                format=$format,width=$width,height=$height,orientation=$orientation,captured=$captured,
                metadata_revision=$metadata_revision,content_revision=$revision,tool_version=$tool_version,hash=$hash,hash_revision=$hash_revision,
                verified=$verified WHERE id=$id;
            """, ("$id", id), ("$revision", revision.Key), ("$length", revision.Length), ("$links", revision.Links), ("$available", revision.ContentAvailable ? 1 : 0),
            ("$status", status), ("$format", metadata?.Format), ("$width", metadata?.Width), ("$height", metadata?.Height), ("$orientation", metadata?.Orientation), ("$captured", metadata?.CaptureTime),
            ("$metadata_revision", metadata is { Error: null } ? revision.Key : null), ("$tool_version", metadata?.ToolVersion), ("$hash", hash), ("$hash_revision", hash is null ? null : revision.Key), ("$verified", DateTime.UtcNow.ToString("O")));
    }
    public void MarkUnresolved(string id, string message) => Execute("UPDATE objects SET hash_revision=NULL,metadata_revision=NULL,content_revision=NULL,verified=NULL,status=$status WHERE id=$id", ("$id", id), ("$status", message));
    public void StoreHash(string id, MediaRevision revision, string hash) => Execute("UPDATE objects SET hash=$hash,hash_revision=$revision,status='Default stream verified',verified=$now WHERE id=$id AND revision=$revision", ("$id", id), ("$hash", hash), ("$revision", revision.Key), ("$now", DateTime.UtcNow.ToString("O")));
    public string ReviewLocations(long collection, PhotoRow row, PhotoMode mode)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            var filter = mode == PhotoMode.Duplicates
                ? "o.hash=$hash AND o.hash_revision=o.revision AND o.default_bytes=$bytes"
                : mode == PhotoMode.Families ? "l.parent=$parent AND l.stem=$stem" : "o.id=$id";
            command.CommandText = $"SELECT l.path,l.role,o.id FROM locations l JOIN objects o ON o.id=l.object_id WHERE l.collection_id=$collection AND l.missing=0 AND {filter} ORDER BY l.path COLLATE BINARY LIMIT 33";
            Add(command, "$collection", collection);
            if (mode == PhotoMode.Duplicates) { Add(command, "$hash", row.Hash); Add(command, "$bytes", row.DefaultBytes); }
            else if (mode == PhotoMode.Families) { Add(command, "$parent", Path.GetDirectoryName(row.Path)); Add(command, "$stem", PhotoKinds.Stem(row.Path)); }
            else Add(command, "$id", row.ObjectId);
            using var reader = command.ExecuteReader(); var lines = new List<string>(33);
            while (reader.Read()) lines.Add($"{reader.GetString(0)}\n  {reader.GetString(1)}{(reader.GetString(2) == row.ObjectId ? " · Same physical file as selection" : "")}");
            return "\n\nRelated locations (within this collection):\n" + string.Join("\n", lines.Take(32)) + (lines.Count > 32 ? "\nMore locations are available in the paged list." : "");
        }
    }
    public string StartJob(long collection, string kind)
    {
        var id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO jobs(id,collection_id,kind,state,started,updated) VALUES($id,$collection,$kind,'Running',$now,$now)", ("$id", id), ("$collection", collection), ("$kind", kind), ("$now", DateTime.UtcNow.ToString("O"))); return id;
    }
    public void JobState(string job, string state, long completed, string? message = null) => Execute("UPDATE jobs SET state=$state,completed=$completed,updated=$now,message=$message WHERE id=$id", ("$id", job), ("$state", state), ("$completed", completed), ("$now", DateTime.UtcNow.ToString("O")), ("$message", message));
    public string? LastJob(long collection)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT kind || ' · ' || state || ' · ' || completed || CASE WHEN kind='Discovery' THEN ' locations' ELSE ' checks' END FROM jobs WHERE collection_id=$collection ORDER BY started DESC LIMIT 1";
            Add(command, "$collection", collection); return command.ExecuteScalar() as string;
        }
    }
    private void Execute(string sql, params (string Name, object? Value)[] values)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var value in values) Add(command, value.Name, value.Value); command.ExecuteNonQuery();
        }
    }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? String(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
    private static int? Integer(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);
    private static long? Long(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
    public static string UnknownIdentity(string path) => "uncertain:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    public void Dispose() { using var connection = new SqliteConnection(_connectionString); SqliteConnection.ClearPool(connection); }
}

internal static class PhotoFormatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

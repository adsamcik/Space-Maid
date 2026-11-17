using Microsoft.Data.Sqlite;

namespace SpaceMaid.Extensions.Photos;

internal sealed partial class PhotoCatalog
{
    public void ChangeCollectionRoot(PhotoCollection expected, string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            RequireIdleCollection(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM collections WHERE root=$root AND id<>$id";
            Add(command, "$root", root); Add(command, "$id", expected.Id);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidOperationException("That folder is already a collection. Select it instead.");
            // Only unshared objects lose their old proof here. Other collection
            // indexes and physical-copy preferences are never deleted.
            command.CommandText = """
                UPDATE objects SET content_revision=NULL,hash_revision=NULL,metadata_revision=NULL,
                    status='Earlier location removed · Refresh and Analyze to revalidate'
                WHERE id IN (SELECT object_id FROM locations WHERE collection_id=$id)
                    AND NOT EXISTS(SELECT 1 FROM locations x WHERE x.object_id=objects.id AND x.collection_id<>$id AND x.missing=0);
                DELETE FROM locations WHERE collection_id=$id;
                DELETE FROM jobs WHERE collection_id=$id;
                UPDATE collections SET root=$root,name=$name,root_version=$version,status='Not discovered',observed=NULL,generation=NULL,
                    coverage='Collection location changed. Refresh filenames, then Analyze to verify content.' WHERE id=$id;
                """;
            Add(command, "$name", Path.GetFileName(root) is { Length: > 0 } name ? name : root);
            Add(command, "$version", Guid.NewGuid().ToString("N"));
            command.ExecuteNonQuery(); transaction.Commit();
        }
    }

    public void RemoveCollection(PhotoCollection expected)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            RequireIdleCollection(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM locations WHERE collection_id=$id; DELETE FROM jobs WHERE collection_id=$id; DELETE FROM collections WHERE id=$id;";
            Add(command, "$id", expected.Id); command.ExecuteNonQuery(); transaction.Commit();
            // Global objects/keep preferences remain available to other
            // collections and to a later explicit re-add of the same files.
        }
    }

    public string StartDiscovery(PhotoCollection expected, string generation)
        => StartCollectionJobCore(expected, "Discovery", generation);

    public string StartCollectionJob(PhotoCollection expected, string kind)
        => StartCollectionJobCore(expected, kind, null);

    public void ObserveCollection(PhotoCollection expected, string job, string generation, IReadOnlyList<PhotoObservation> observations)
        => ObserveCore(expected.Id, generation, observations, expected, job);

    public bool TryEndCollectionDiscovery(PhotoCollection expected, string job, string generation, bool complete, string status, string? coverage = null)
        => EndDiscoveryCore(expected.Id, generation, complete, status, coverage, expected, job);

    public IReadOnlyList<PhotoRow> AnalysisCollectionBatch(PhotoCollection expected, string job, string? after, bool candidatesOnly, CancellationToken token)
        => AnalysisBatchCore(expected.Id, after, candidatesOnly, token, expected, job);

    public int StoreCollectionAnalysis(PhotoCollection expected, string job, IReadOnlyList<PhotoAnalysisUpdate> updates, CancellationToken token)
        => StoreAnalysisCore(updates, token, expected, job);

    public bool TryCollectionJobState(PhotoCollection expected, string job, string state, long completed, string? message = null)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            if (!CollectionJobCurrent(connection, transaction, expected, job, null)) return false;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE jobs SET state=$state,completed=$completed,updated=$now,message=$message WHERE id=$id";
            Add(command, "$id", job); Add(command, "$state", state); Add(command, "$completed", completed);
            Add(command, "$now", DateTime.UtcNow.ToString("O")); Add(command, "$message", message);
            command.ExecuteNonQuery(); transaction.Commit(); return true;
        }
    }

    private static bool CollectionJobCurrent(SqliteConnection connection, SqliteTransaction? transaction, PhotoCollection expected, string job, string? generation)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM collections c JOIN jobs j ON j.collection_id=c.id
            WHERE c.id=$collection AND c.root=$root AND c.root_version IS $version
                AND j.id=$job AND j.state='Running' AND ($generation IS NULL OR c.generation=$generation)
            """;
        Add(command, "$collection", expected.Id); Add(command, "$root", expected.Root); Add(command, "$version", expected.RootVersion);
        Add(command, "$job", job); Add(command, "$generation", generation);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private string StartCollectionJobCore(PhotoCollection expected, string kind, string? generation)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            RequireIdleCollection(connection, transaction, expected);
            if (generation is not null) BeginDiscoveryCore(connection, transaction, expected.Id, generation);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            Add(command, "$collection", expected.Id);
            string id = Guid.NewGuid().ToString("N");
            command.CommandText = "INSERT INTO jobs(id,collection_id,kind,state,started,updated) VALUES($id,$collection,$kind,'Running',$now,$now)";
            Add(command, "$id", id); Add(command, "$kind", kind); Add(command, "$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery(); transaction.Commit(); return id;
        }
    }

    private static void BeginDiscoveryCore(SqliteConnection connection, SqliteTransaction transaction, long collection, string generation)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE objects SET content_revision=NULL,hash_revision=NULL,metadata_revision=NULL,
                status='Earlier observation · Refresh incomplete; Analyze to revalidate content'
                WHERE id IN (SELECT object_id FROM locations WHERE collection_id=$collection AND missing=0);
            UPDATE collections SET status='Discovering',generation=$generation WHERE id=$collection;
            """;
        Add(command, "$collection", collection); Add(command, "$generation", generation); command.ExecuteNonQuery();
    }

    private static void RequireIdleCollection(SqliteConnection connection, SqliteTransaction transaction, PhotoCollection expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT root,root_version FROM collections WHERE id=$id"; Add(command, "$id", expected.Id);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || !reader.GetString(0).Equals(expected.Root, StringComparison.Ordinal)
                || !string.Equals(String(reader, 1), expected.RootVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("This collection changed or was removed. Select its current location before continuing.");
        }
        command.CommandText = "SELECT COUNT(*) FROM jobs WHERE collection_id=$id AND state='Running'";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw new InvalidOperationException("Finish or stop this collection's active job before changing its index or starting another job.");
    }
}

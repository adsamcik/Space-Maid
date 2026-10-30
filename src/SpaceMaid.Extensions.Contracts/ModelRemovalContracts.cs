using System.Windows;

namespace SpaceMaid.Extensions.Contracts;

public sealed record ModelRemovalRequest(string LibraryPath, string Name, string Digest, long Bytes,
    string ProbeName, string ProbeDigest, string ProbeBlobDigest);
public sealed record ModelRemovalResult(bool Deleted, bool Attempted, string Message);

// The model extension owns review, service validation and deletion for both views.
public interface IModelRemovalWorkflow
{
    Task<ModelRemovalResult> ReviewModelRemovalAsync(ModelRemovalRequest model, Window owner, CancellationToken token);
}

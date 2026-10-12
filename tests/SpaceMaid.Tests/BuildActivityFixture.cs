using System.IO;

namespace SpaceMaid.Tests;

internal static class BuildActivityFixture
{
    // Build cleanup uses metadata activity, including creation dates and source
    // files beside the output. Keep fixtures old by dating their actual contents.
    internal static void AgeProject(string root, int days = 14)
    {
        var date = DateTime.UtcNow.AddDays(-days);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, date);
            File.SetLastWriteTimeUtc(file, date);
        }
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            Directory.SetCreationTimeUtc(directory, date);
            Directory.SetLastWriteTimeUtc(directory, date);
        }
    }
}

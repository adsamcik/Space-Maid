using System.IO;

namespace SpaceMaid.App;

internal sealed record FolderCrumb(string Name, string Path);

internal static class PathNavigation
{
    internal static string Normalize(string input, string current)
    {
        string path = input.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        path = Environment.ExpandEnvironmentVariables(path);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Enter a folder path.");
        // A bare drive letter means its root, never that drive's working directory.
        if (path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path += "\\";
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, current));
    }

    internal static IReadOnlyList<FolderCrumb> Crumbs(string path)
    {
        string root = Path.GetPathRoot(path)!;
        var result = new List<FolderCrumb> { new(root, root) };
        string current = root;
        foreach (string part in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            result.Add(new(part, current));
        }
        return result;
    }
}

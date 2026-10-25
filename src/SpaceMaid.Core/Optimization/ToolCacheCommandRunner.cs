using System.Diagnostics;
using System.Text;

namespace SpaceMaid.Core.Optimization;

internal static class ToolCacheCommandRunner
{
    internal const int OutputLimit = 256 * 1024;

    internal static async Task<ToolCacheCommandResult> RunAsync(ToolCacheCommand command, CancellationToken token)
    {
        ToolCachePaths.ValidateFile(command.Executable);
        if (!Path.GetExtension(command.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only direct executable launchers are supported; shell wrappers are not executed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(command.Timeout);
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in command.Environment) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new IOException("The native tool did not start.");
        using var cancel = deadline.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        int count = 0;
        async Task<string> ReadAsync(StreamReader reader)
        {
            var output = new StringBuilder(); var buffer = new char[2048];
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (read == 0) return output.ToString();
                if (Interlocked.Add(ref count, read) > OutputLimit)
                { deadline.Cancel(); throw new InvalidDataException("The native tool exceeded the bounded output limit."); }
                output.Append(buffer, 0, read);
            }
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (count > OutputLimit) throw new InvalidDataException("The native tool exceeded the bounded output limit.");
            throw new IOException("The native tool exceeded its time limit; inspect again before retrying.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
    }

    internal static ToolCacheLauncher? Resolve(ToolCacheKind kind)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(128)
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? Find(string file) => directories.Select(path => Path.Join(path, file)).FirstOrDefault(SafeFile);
        if (kind is ToolCacheKind.NuGet or ToolCacheKind.Cargo)
        {
            string? exe = Find(kind == ToolCacheKind.NuGet ? "dotnet.exe" : "cargo.exe");
            return exe is null ? null : new(exe, Array.Empty<string>());
        }
        if (kind == ToolCacheKind.Gradle) return null; // Gradle launchers evaluate init scripts; inspect policy files instead.
        string tool = kind == ToolCacheKind.Npm ? "npm" : "pnpm";
        string? direct = Find(tool + ".exe");
        if (direct is not null) return new(direct, Array.Empty<string>());
        string? node = Find("node.exe");
        if (node is null) return null;
        foreach (string directory in directories.Prepend(Path.GetDirectoryName(node)!))
        {
            string script = kind == ToolCacheKind.Npm
                ? Path.Join(directory, "node_modules", "npm", "bin", "npm-cli.js")
                : Path.Join(directory, "node_modules", "pnpm", "bin", "pnpm.cjs");
            if (SafeFile(script)) return new(node, Array.AsReadOnly(new[] { script }));
        }
        return null;
    }

    private static bool SafeFile(string path)
    {
        try { ToolCachePaths.ValidateFile(OptimizationPolicy.NormalizePath(path)); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
}

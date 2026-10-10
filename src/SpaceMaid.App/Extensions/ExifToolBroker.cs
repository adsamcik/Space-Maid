using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed class ExifToolBroker : IToolBroker, IAsyncDisposable
{
    private const int MaximumReplyCharacters = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<CancellationToken, Task> _checkpoint;
    private readonly Action<string, object> _log;
    private readonly bool _administrator;
    private readonly string _executable;
    private Process? _process;
    private Task? _stderr;
    private string? _version;
    private long _sequence;
    private int _stderrCharacters;
    private volatile bool _disposed;

    internal ExifToolBroker(Func<CancellationToken, Task> checkpoint, Action<string, object> log, bool administrator,
        string? executable = null)
    {
        _checkpoint = checkpoint; _log = log; _administrator = administrator;
        _executable = executable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ExifTool", "ExifTool.exe");
    }
    public async Task<string?> ExifToolVersionAsync(CancellationToken token)
    {
        ThrowIfUnavailable();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = requestCancellation.Token;
        if (!File.Exists(_executable)) return null;
        await _checkpoint(token).ConfigureAwait(false);
        await _requests.WaitAsync(token).ConfigureAwait(false);
        try { await EnsureStartedAsync(token).ConfigureAwait(false); return _version; }
        catch { await StopOwnedProcessAsync().ConfigureAwait(false); throw; }
        finally { _requests.Release(); }
    }
    public async Task<IReadOnlyList<MediaToolMetadata>> ReadExifMetadataAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        ThrowIfUnavailable();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = requestCancellation.Token;
        if (paths.Count > 32) throw new ArgumentException("ExifTool requests contain at most 32 selected files.", nameof(paths));
        foreach (string path in paths) ValidateInput(path);
        if (paths.Count == 0) return [];
        if (!File.Exists(_executable)) return paths.Select(path => new MediaToolMetadata(path, null, null, null, null, null, "unavailable", "ExifTool is not installed at the approved local location.")).ToArray();
        await _checkpoint(token).ConfigureAwait(false);
        await _requests.WaitAsync(token).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        try
        {
            // Revalidate after queue waiting; never let the tool hydrate a file
            // that became cloud-only or was replaced by a reparse point.
            foreach (string path in paths) ValidateInput(path);
            using var stableInputs = new ToolInputLease(paths);
            await EnsureStartedAsync(token).ConfigureAwait(false);
            string[] options = ["-json", "-FileType", "-ImageWidth", "-ImageHeight", "-Orientation#", "-DateTimeOriginal", "-SourceFile", "-Error", "-Warning", "-charset", "filename=UTF8"];
            string reply = await RequestAsync(options.Concat(paths), token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(reply);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ExifTool returned an unexpected metadata response.");
            var result = new List<MediaToolMetadata>(paths.Count);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                string path = MatchReturnedPath(paths, Text(row, "SourceFile"));
                result.Add(new(path, Text(row, "FileType"), Number(row, "ImageWidth"), Number(row, "ImageHeight"), Number(row, "Orientation"),
                    Text(row, "DateTimeOriginal"), _version!, Text(row, "Error")));
            }
            foreach (string path in paths)
                if (!result.Any(row => row.Path == path)) result.Add(new(path, null, null, null, null, null, _version!, "ExifTool did not return metadata for this file."));
            _log("extension-tool-metadata", new { Tool = "ExifTool", Version = _version, Count = paths.Count, ElapsedMs = timer.Elapsed.TotalMilliseconds, Errors = result.Count(value => value.Error is not null) });
            return result;
        }
        catch
        { await StopOwnedProcessAsync().ConfigureAwait(false); throw; }
        finally { _requests.Release(); }
    }
    internal static string MatchReturnedPath(IReadOnlyList<string> paths, string? returnedPath)
    {
        string? normalized = returnedPath?.Replace('/', '\\');
        string? exact = paths.FirstOrDefault(candidate => candidate.Equals(normalized, StringComparison.Ordinal));
        if (exact is not null) return exact;
        // NTFS folders can be case-sensitive. A case-insensitive fallback is
        // valid only when it cannot attach metadata to a different file.
        string? match = null;
        foreach (string candidate in paths)
        {
            if (!candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) throw new InvalidDataException("ExifTool returned an ambiguous file path.");
            match = candidate;
        }
        return match ?? throw new InvalidDataException("ExifTool returned a file outside the requested metadata batch.");
    }
    private async Task EnsureStartedAsync(CancellationToken token)
    {
        if (_process is { HasExited: false } && _version is not null) return;
        await StopOwnedProcessAsync().ConfigureAwait(false);
        if ((File.GetAttributes(_executable) & FileAttributes.ReparsePoint) != 0) throw new IOException("The approved ExifTool executable is a reparse file.");
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-stay_open"); start.ArgumentList.Add("True"); start.ArgumentList.Add("-@"); start.ArgumentList.Add("-");
        _process = Process.Start(start) ?? throw new IOException("ExifTool could not be started.");
        _stderr = DrainErrorAsync(_process);
        string version = (await RequestAsync(["-ver"], token).ConfigureAwait(false)).Trim();
        if (version.Length > 40 || !decimal.TryParse(version, System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("ExifTool returned an unsupported version response.");
        _version = version;
        _log("extension-tool-start", new { Tool = "ExifTool", Executable = _executable, Version = _version, _process.Id });
    }
    private async Task<string> RequestAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var process = _process ?? throw new IOException("ExifTool is unavailable.");
        Interlocked.Exchange(ref _stderrCharacters, 0);
        long sequence = ++_sequence;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        try
        {
            foreach (string argument in arguments) await process.StandardInput.WriteLineAsync(argument.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(("-execute" + sequence).AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reply = new StringBuilder(); var line = new StringBuilder(); char[] buffer = new char[1];
            string marker = "{ready" + sequence + "}";
            while (true)
            {
                int read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (read == 0) throw new IOException("ExifTool exited before completing its request.");
                if (buffer[0] != '\n')
                {
                    if (reply.Length + line.Length >= MaximumReplyCharacters) throw new InvalidDataException("ExifTool metadata exceeded the 2 MiB reply limit.");
                    if (buffer[0] != '\r') line.Append(buffer[0]);
                    continue;
                }
                string value = line.ToString(); line.Clear();
                if (value == marker) return reply.ToString();
                reply.AppendLine(value);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("ExifTool metadata request exceeded 45 seconds."); }
    }
    private async Task DrainErrorAsync(Process process)
    {
        // Drain a bounded amount without logging extracted metadata. A noisy
        // process is stopped instead of retaining unlimited stderr.
        char[] buffer = new char[4096];
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                if (Interlocked.Add(ref _stderrCharacters, read) > 64 * 1024) { if (!process.HasExited) process.Kill(true); return; }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    internal static void ValidateInput(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0 || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Tool inputs must be absolute local paths without control characters.");
        string? parent = Path.GetDirectoryName(path);
        if (parent is null) throw new IOException("The file has no local parent folder.");
        ExtensionInventory.ValidateAncestors(parent);
        var attributes = File.GetAttributes(path);
        const FileAttributes cloudUnavailable = (FileAttributes)(0x1000 | 0x40000 | 0x400000);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | cloudUnavailable)) != 0)
            throw new IOException("Cloud-only, reparse and unavailable files remain metadata-only; ExifTool was not started for this file.");
    }
    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static int? Number(JsonElement row, string name) => row.TryGetProperty(name, out var value)
        && int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : null;
    private void ThrowIfUnavailable()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (_administrator) throw new InvalidOperationException("Media tools cannot run while the Space Maid host is elevated."); }
    private async Task StopOwnedProcessAsync()
    {
        var process = _process; _process = null; _version = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (_stderr is not null) await _stderr.ConfigureAwait(false);
        _stderr = null; process.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _shutdown.Cancel();
        await _requests.WaitAsync().ConfigureAwait(false);
        try { await StopOwnedProcessAsync().ConfigureAwait(false); }
        finally { _requests.Release(); _requests.Dispose(); _shutdown.Dispose(); }
    }
}

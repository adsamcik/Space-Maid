using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SpaceMaid.App.AgentAccess;

internal sealed class AgentConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _first;
    private readonly Task _run;
    internal string PipeName { get; } = "SpaceMaid-Agent-" + Guid.NewGuid().ToString("N");
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal bool IsRunning => !_run.IsCompleted;

    internal AgentConnection(Func<string, JsonElement, Task<object>> call, AgentAccessLevel level)
    {
        // Bind before publishing the connection configuration.
        _first = CreatePipe();
        _run = RunAsync(call, level);
    }
    private NamedPipeServerStream CreatePipe() => new(PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunAsync(Func<string, JsonElement, Task<object>> call, AgentAccessLevel level)
    {
        NamedPipeServerStream? pipe = _first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    try
                    {
                        using var reader = Reader(pipe);
                        using var writer = Writer(pipe);
                        using var auth = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        auth.CancelAfter(TimeSpan.FromSeconds(5));
                        string? supplied = await ReadLineAsync(reader, auth.Token);
                        if (supplied is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(Secret)))
                            throw new IOException("Agent authentication failed.");
                        await writer.WriteLineAsync("ready".AsMemory(), _stop.Token);
                        var protocol = new AgentProtocol(call, level);
                        for (int requests = 0; requests < 512; requests++)
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            deadline.CancelAfter(TimeSpan.FromMinutes(10));
                            string? line = await ReadLineAsync(reader, deadline.Token);
                            if (line is null) break;
                            string? reply = await protocol.HandleAsync(line);
                            if (reply is not null) await writer.WriteLineAsync(reply.AsMemory(), deadline.Token);
                        }
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or DecoderFallbackException) { }
                }
                if (!_stop.IsCancellationRequested) pipe = CreatePipe();
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { }
        finally { pipe?.Dispose(); }
    }

    internal string Configuration(string executable) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            ["space-maid"] = new { command = executable, args = new[] { "--agent-stdio" },
                env = new { SPACEMAID_AGENT_PIPE = PipeName, SPACEMAID_AGENT_TOKEN = Secret } }
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    // The app executable doubles as a stdio bridge. It never initializes WPF,
    // extensions, updater, scanner or an elevated helper in this mode.
    internal static async Task<int> RunStdioAsync()
    {
        if (Elevation.IsAdministrator) return 2;
        string? name = Environment.GetEnvironmentVariable("SPACEMAID_AGENT_PIPE");
        string? secret = Environment.GetEnvironmentVariable("SPACEMAID_AGENT_TOKEN");
        if (name is null || !name.StartsWith("SpaceMaid-Agent-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[16..], "N", out _) || secret?.Length != 64) return 2;
        Environment.SetEnvironmentVariable("SPACEMAID_AGENT_TOKEN", null);
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                TokenImpersonationLevel.Anonymous);
            await pipe.ConnectAsync(5000);
            using var reader = Reader(pipe);
            // AutoFlush writes every frame. This leave-open wrapper owns only
            // managed buffers; disposing it after revocation would flush a
            // broken/disposed pipe. The pipe below owns the native lifetime.
            var writer = Writer(pipe);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await writer.WriteLineAsync(secret.AsMemory(), ready.Token);
            if (await ReadLineAsync(reader, ready.Token) != "ready") return 2;
            using var input = Reader(Console.OpenStandardInput()); using var output = Writer(Console.OpenStandardOutput());
            // Duplex relay lets revocation/host exit end an idle bridge too.
            using var stop = new CancellationTokenSource();
            async Task Relay(StreamReader source, StreamWriter target, int limit)
            {
                while (await ReadLineAsync(source, stop.Token, limit) is { } line)
                    await target.WriteLineAsync(line.AsMemory(), stop.Token);
            }
            var incoming = Relay(input, writer, 16384); var outgoing = Relay(reader, output, 131072);
            var completed = await Task.WhenAny(incoming, outgoing);
            stop.Cancel(); pipe.Dispose();
            // Windows console stdin can be an uninterruptible synchronous read.
            // Do not keep an idle bridge alive after the host revokes its pipe.
            // Main returns and owns process termination; observe any late fault.
            _ = Task.WhenAll(incoming, outgoing).ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            try { await completed; }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or DecoderFallbackException) { return 2; }
    }

    internal static StreamReader Reader(Stream stream) => new(stream, new UTF8Encoding(false, true), false, 4096, true);
    internal static StreamWriter Writer(Stream stream) => new(stream, new UTF8Encoding(false, true), 4096, true) { AutoFlush = true, NewLine = "\n" };
    internal static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token, int limit = 16384)
    {
        var line = new StringBuilder(); var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= limit) throw new IOException("Agent frame limit exceeded.");
            line.Append(character[0]);
        }
        if (line.Length > 0) throw new IOException("Incomplete agent frame.");
        return null;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _run;
        _stop.Dispose();
    }
}

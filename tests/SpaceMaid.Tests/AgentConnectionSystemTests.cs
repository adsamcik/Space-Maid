using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using SpaceMaid.App;
using SpaceMaid.App.AgentAccess;
using Xunit;

namespace SpaceMaid.Tests;

[Trait("Stage", "System")]
public sealed class AgentConnectionSystemTests
{
    [Fact]
    public async Task RealStdioBridgeAuthenticatesServesMcpAndExitsOnRevocation()
    {
        var connection = new AgentConnection((_, _) => Task.FromResult<object>(new { files = 4, logicalBytes = 8192 }), AgentAccessLevel.Summary);
        Process? process = null;
        try
        {
            string executable = Path.ChangeExtension(typeof(SpaceMaid.App.App).Assembly.Location, ".exe");
            Assert.True(File.Exists(executable));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--agent-stdio");
            start.Environment["SPACEMAID_AGENT_PIPE"] = connection.PipeName;
            start.Environment["SPACEMAID_AGENT_TOKEN"] = connection.Secret;
            process = Process.Start(start)!;
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{}}}");
            string initialization = (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            Assert.Contains("protocolVersion", initialization);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_summary\"}}");
            string result = (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            using var document = JsonDocument.Parse(result);
            Assert.Equal(8192, document.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("logicalBytes").GetInt32());
            Assert.DoesNotContain(connection.Secret, initialization + result);
            await connection.DisposeAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException error)
            {
                process.Kill(); await process.WaitForExitAsync();
                throw new InvalidOperationException("Bridge did not stop after revocation. " + await process.StandardError.ReadToEndAsync(), error);
            }
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(); await process.WaitForExitAsync(); }
            process?.Dispose();
            if (connection.IsRunning) await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task BadCredentialsCannotInvokeToolsAndDoNotBreakTheNextConnection()
    {
        int calls = 0;
        await using var connection = new AgentConnection((_, _) => { calls++; return Task.FromResult<object>(new { }); }, AgentAccessLevel.Summary);
        using (var bad = new NamedPipeClientStream(".", connection.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await bad.ConnectAsync(5000);
            using var writer = AgentConnection.Writer(bad); using var reader = AgentConnection.Reader(bad);
            await writer.WriteLineAsync("incorrect");
            Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        using var good = new NamedPipeClientStream(".", connection.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await good.ConnectAsync(5000);
        using var authorized = AgentConnection.Writer(good); using var response = AgentConnection.Reader(good);
        await authorized.WriteLineAsync(connection.Secret);
        Assert.Equal("ready", await response.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);
    }
}

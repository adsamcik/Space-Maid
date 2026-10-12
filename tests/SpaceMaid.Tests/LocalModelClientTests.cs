using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using SpaceMaid.LocalModel;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class LocalModelClientTests
{
    [Fact]
    public async Task StandardClientReusesOneOwnedWorkerAndTransfersOnlyTextAndBoundedOptions()
    {
        using var worker = new FakeWorker(Frames(
            new ModelReply(1, 1, "{\"action\":\"done\"}"), new ModelReply(1, 2, "{}")));
        int starts = 0;
        using var client = new IsolatedModelChatClient(() => { starts++; return worker; }, TimeSpan.FromSeconds(5));
        var response = await client.GetResponseAsync([new(ChatRole.System, "Host observations"), new(ChatRole.User, "Review")],
            new ChatOptions { MaxOutputTokens = 500, Temperature = 0.2f, ResponseFormat = ChatResponseFormat.Json });
        Assert.Equal("{\"action\":\"done\"}", response.Text);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "Again")])) updates.Add(update);
        Assert.Single(updates);
        Assert.Equal("{}", updates[0].Text);
        Assert.Equal(1, starts);
        Assert.False(worker.Disposed);

        worker.Input.Position = 0;
        var reader = new ModelFrameReader(worker.Input, ModelProtocol.MaximumRequestBytes);
        var first = ModelProtocol.Deserialize<ModelRequest>((await reader.ReadAsync(CancellationToken.None))!);
        Assert.Equal("chat", first.Kind);
        Assert.Equal(500, first.MaxOutputTokens);
        Assert.Equal(0.2f, first.Temperature);
        Assert.Equal(["system", "user"], first.Messages!.Select(message => message.Role));
        Assert.Null(first.Probe);
        var second = ModelProtocol.Deserialize<ModelRequest>((await reader.ReadAsync(CancellationToken.None))!);
        Assert.Equal(2, second.Id);

        Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Null(client.GetService(typeof(Stream)));
        Assert.Null(client.GetService(typeof(IChatClient), "arbitrary service"));
        client.Dispose();
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData("{\"version\":1,\"id\":2,\"text\":\"{}\"}\n")]
    [InlineData("{\"version\":2,\"id\":1,\"text\":\"{}\"}\n")]
    [InlineData("{\"version\":1,\"id\":1,\"error\":\"anything\"}\n")]
    [InlineData("{\"version\":1,\"id\":1}\n")]
    [InlineData("{\"version\":1,\"id\":1,\"text\":\"{}\",\"execute\":true}\n")]
    [InlineData("{\"version\":1,\"id\":1,\"id\":2,\"text\":\"{}\"}\n")]
    [InlineData("not JSON\n")]
    [InlineData("{\"version\":1,\"id\":1,\"text\":\"{}\"}")]
    public async Task MalformedWorkerReplyPublishesNothingAndStopsOwnedSession(string reply)
    {
        using var worker = new FakeWorker(Encoding.UTF8.GetBytes(reply));
        using var client = new IsolatedModelChatClient(() => worker, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new(ChatRole.User, "Review")]));
        Assert.True(worker.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "Retry")]));
    }

    [Fact]
    public async Task OverlongWorkerFrameStopsWithoutBuildingAnUnboundedReply()
    {
        using var worker = new FakeWorker(Encoding.UTF8.GetBytes(new string('x', ModelProtocol.MaximumResponseBytes + 1) + "\n"));
        using var client = new IsolatedModelChatClient(() => worker, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetResponseAsync([new(ChatRole.User, "Review")]));
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCancellationOrTimeoutStopsWorkerAndRejectsTheTurn(bool cancellation)
    {
        using var worker = new FakeWorker(new GateReadStream([]));
        using var client = new IsolatedModelChatClient(() => worker,
            cancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(30));
        using var cancel = new CancellationTokenSource();
        var response = client.GetResponseAsync([new(ChatRole.User, "Review")], cancellationToken: cancel.Token);
        if (cancellation) cancel.Cancel();
        if (cancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        else await Assert.ThrowsAsync<TimeoutException>(() => response);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task CancellingAQueuedTurnDoesNotStopAnotherActiveTurn()
    {
        var output = new GateReadStream(Frames(new ModelReply(1, 1, "{}")));
        using var worker = new FakeWorker(output);
        using var client = new IsolatedModelChatClient(() => worker, TimeSpan.FromSeconds(5));
        var active = client.GetResponseAsync([new(ChatRole.User, "Review")]);
        await output.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var queued = client.GetResponseAsync([new(ChatRole.User, "Next")], cancellationToken: cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(worker.Disposed);
        output.Release.TrySetResult();
        Assert.Equal("{}", (await active).Text);
        Assert.False(worker.Disposed);
    }

    [Fact]
    public async Task InvalidInputAndNativeOrExecutableOptionsNeverStartAWorker()
    {
        int starts = 0;
        using var client = new IsolatedModelChatClient(() => { starts++; throw new InvalidOperationException(); }, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([new(ChatRole.Tool, "Command")]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([new(ChatRole.User,
            [new FunctionCallContent("call", "delete", new Dictionary<string, object?>())])]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([new(ChatRole.User, "Review")],
            new ChatOptions { RawRepresentationFactory = _ => new object() }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetResponseAsync([new(ChatRole.User, "Review")],
            new ChatOptions { MaxOutputTokens = 1025 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetResponseAsync([new(ChatRole.User, "Review")],
            new ChatOptions { Temperature = float.NaN }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([new(ChatRole.User,
            new string('x', ModelProtocol.MaximumInputCharacters + 1))]));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task FrameReaderRetainsNextFrameAndRejectsDuplicateNestedProperties()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1}\n{\"version\":2}\n"));
        var reader = new ModelFrameReader(stream, 128);
        Assert.Equal("{\"version\":1}", Encoding.UTF8.GetString((await reader.ReadAsync(CancellationToken.None))!));
        Assert.Equal("{\"version\":2}", Encoding.UTF8.GetString((await reader.ReadAsync(CancellationToken.None))!));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ModelProtocol.Deserialize<ModelRequest>(Encoding.UTF8.GetBytes(
            "{\"version\":1,\"id\":1,\"kind\":\"chat\",\"messages\":[{\"role\":\"user\",\"text\":\"a\",\"Text\":\"b\"}]}")));
    }

    [Fact]
    public async Task EscapedWireLimitIsEnforcedBeforeStartingAWorker()
    {
        int starts = 0;
        using var client = new IsolatedModelChatClient(() => { starts++; throw new InvalidOperationException(); }, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetResponseAsync([new(ChatRole.User,
            new string('\u0001', ModelProtocol.MaximumInputCharacters))]));
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData("context_limit", "smaller folder")]
    [InlineData("runtime_unavailable", "Repair the app")]
    public async Task KnownWorkerErrorProvidesRecoveryWithoutReturningNativeDiagnostics(string code, string recovery)
    {
        using var worker = new FakeWorker(Frames(new ModelReply(1, 1, Error: code)));
        using var client = new IsolatedModelChatClient(() => worker, TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "Review")]));
        Assert.Contains(recovery, error.Message);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task WorkerInferenceTimeoutRequestsASmallerModelAndStopsTheOwnedWorker()
    {
        using var worker = new FakeWorker(Frames(new ModelReply(1, 1, Error: "inference_timeout")));
        using var client = new IsolatedModelChatClient(() => worker, TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetResponseAsync([new(ChatRole.User, "Review")]));
        Assert.Contains("smaller model", error.Message);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public void StatefulContextContinuesOnlyTheExactPreviousConversationAndGeneratedReply()
    {
        ModelMessage[] original = [new("system", "Bounded review"), new("user", "Selected folder")];
        const string generated = "{\"kind\":\"summary\"}";
        ModelMessage[] appended = [.. original, new("assistant", generated), new("user", "Measured facts")];
        Assert.True(ModelProtocol.IsExactContinuation(original, generated, appended));
        Assert.False(ModelProtocol.IsExactContinuation(null, generated, appended));
        Assert.False(ModelProtocol.IsExactContinuation(original, null, appended));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [new("system", "Changed policy"), .. appended[1..]]));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [.. original, new("user", "Measured facts")]));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [.. original, new("assistant", "Edited reply"), appended[^1]]));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [.. original, new("user", generated), appended[^1]]));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [.. original, appended[^2], new("system", "Unrelated instruction")]));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [.. appended, new("user", "Extra message")]));
        Assert.False(ModelProtocol.IsExactContinuation(original, generated, [new("user", "Independent history")]));
    }

    [Fact]
    public void StatefulTemplateSuffixPreservesThePendingTerminalMarkerAndExactFormatting()
    {
        const string previous = "<system>policy</system><user>first</user><assistant>";
        const string generated = "{\"kind\":\"summary\"}";
        const string terminal = "</assistant>";
        const string suffix = "<user>measured facts</user><assistant>\n";
        string full = previous + generated + terminal + suffix;
        Assert.True(ModelProtocol.TryGetTemplateSuffix(previous, generated, terminal, full, out string actual));
        Assert.Equal(suffix, actual);
        Assert.False(ModelProtocol.TryGetTemplateSuffix("edited", generated, terminal, full, out _));
        Assert.False(ModelProtocol.TryGetTemplateSuffix(previous, "changed reply", terminal, full, out _));
        Assert.False(ModelProtocol.TryGetTemplateSuffix(previous, generated, "different terminator", full, out _));
        Assert.False(ModelProtocol.TryGetTemplateSuffix(previous, generated, "", full, out _));
        Assert.False(ModelProtocol.TryGetTemplateSuffix(previous, generated, terminal, full.TrimEnd('\n'), out _));
        Assert.False(ModelProtocol.TryGetTemplateSuffix(previous, generated, terminal, previous + generated + terminal, out _));
    }

    private static byte[] Frames(params ModelReply[] replies) => replies.SelectMany(reply =>
        ModelProtocol.Serialize(reply, ModelProtocol.MaximumResponseBytes).Append((byte)'\n')).ToArray();

    private sealed class FakeWorker : IModelWorkerSession
    {
        public FakeWorker(byte[] replies) : this(new MemoryStream(replies)) { }
        public FakeWorker(Stream output) { StandardOutput = output; }
        public MemoryStream Input { get; } = new();
        public Stream StandardInput => Input;
        public Stream StandardOutput { get; }
        public Stream StandardError { get; } = new MemoryStream();
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); }
    }

    private sealed class GateReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}

using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using Microsoft.Extensions.AI;
using SpaceMaid.LocalModel;
using SpaceMaid.ModelWorker;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    // Reject direct execution before loading the native backend or selected
    // weights. An ordinary subprocess is not an isolation boundary.
    bool probeMode = arguments.Length == 3 && arguments[0] == "--sandbox-probe" && arguments[1] == "--model";
    if (!WorkerIsolation.IsAppContainer(out bool lowPrivilege) || !lowPrivilege) return 10;
    bool modelMode = arguments.Length == 2 && arguments[0] == "--model";
    if (!probeMode && !modelMode) return 11;
    string modelPath = arguments[^1];
    if (!Path.IsPathFullyQualified(modelPath)) return 11;

    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();
    // Preserve the dedicated protocol stream, then send accidental native
    // stdout diagnostics to the separately drained stderr pipe.
    WorkerIsolation.RedirectNativeOutputToError();
    Console.SetOut(TextWriter.Null);
    var reader = new ModelFrameReader(input, ModelProtocol.MaximumRequestBytes);
    LLamaWeights? weights = null;
    LLamaContext? context = null;
    InteractiveExecutor? executor = null;
    IChatClient? client = null;
    PromptTemplateTransformer? transform = null;
    ModelParams? parameters = null;
    IReadOnlyList<ModelMessage>? previousMessages = null;
    string? previousOutput = null;
    string? previousPrompt = null;
    long currentRequestId = 0;
    string failureCode = "inference_failed";
    try
    {
        for (int turn = 0; turn < 16; turn++)
        {
            currentRequestId = 0;
            byte[]? frame = await reader.ReadAsync(CancellationToken.None);
            if (frame is null) return 0;
            var request = ModelProtocol.Deserialize<ModelRequest>(frame);
            if (request.Version != ModelProtocol.Version || request.Id != turn + 1L) return 12;
            ModelReply reply;
            if (probeMode)
            {
                if (request.Kind != "probe" || request.Probe is null || request.Messages is not null || turn != 0 ||
                    !string.Equals(Path.GetFullPath(request.Probe.ModelPath), Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase))
                    return 12;
                currentRequestId = request.Id;
                reply = new(ModelProtocol.Version, request.Id, Probe: await WorkerIsolation.ProbeAsync(request.Probe));
            }
            else
            {
                if (!ValidChat(request)) return 12;
                currentRequestId = request.Id;
                if (weights is null)
                {
                    failureCode = "model_load_failed";
                    NativeLibraryConfig.All.WithCuda(false).WithVulkan(false).WithAutoFallback(false)
                        .WithSearchDirectory(AppContext.BaseDirectory).WithLogCallback((_, _) => { });
                    parameters = new(modelPath)
                    {
                        ContextSize = 8192,
                        GpuLayerCount = 0,
                        NoKqvOffload = true,
                        LoadMode = LLamaLoadMode.MemoryMap,
                        Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                        BatchThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                    };
                    weights = await LLamaWeights.LoadFromFileAsync(parameters);
                    transform = new PromptTemplateTransformer(weights, withAssistant: true);
                }

                var history = new ChatHistory();
                foreach (var message in request.Messages!)
                    history.AddMessage(message.Role == "system" ? AuthorRole.System :
                        message.Role == "assistant" ? AuthorRole.Assistant : AuthorRole.User, message.Text);
                failureCode = "template_unsupported";
                string prompt = transform!.HistoryToText(history);
                int fullTokens = weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
                bool continuation = client is not null && ModelProtocol.IsExactContinuation(previousMessages, previousOutput, request.Messages!);
                long contextTokens = fullTokens;
                string inferencePrompt = prompt;
                int continuationInputTokens = 0;
                if (continuation)
                {
                    var state = (InteractiveExecutor.InteractiveExecutorState)executor!.GetStateData();
                    // The executor retains its sampled EOG as a pending token.
                    // It will decode that marker before the next input. Match
                    // the complete embedded template, including that marker,
                    // so a fresh lone-user template cannot omit or repeat it.
                    continuation = state.Embeds is { Length: 1 } pending && pending[0].IsEndOfGeneration(context!.Vocab)
                        && state.EmbedInps.Length == state.ConsumedTokensCount
                        && ModelProtocol.TryGetTemplateSuffix(previousPrompt, previousOutput,
                            weights.NativeHandle.Vocab.LLamaTokenToString(pending[0], true), prompt, out inferencePrompt);
                    if (continuation)
                    {
                        continuationInputTokens = weights.Tokenize(inferencePrompt, false, true, Encoding.UTF8).Length;
                        contextTokens = (long)state.PastTokensCount + state.Embeds.Length + continuationInputTokens;
                    }
                    else inferencePrompt = prompt;
                }
                if (Math.Max(fullTokens, contextTokens) + request.MaxOutputTokens + 64 > parameters!.ContextSize)
                    reply = new(ModelProtocol.Version, request.Id, Error: "context_limit");
                else
                {
                    failureCode = "inference_failed";
                    if (!continuation)
                    {
                        // AsChatClient does not own the native context. Dispose
                        // it explicitly before resetting an edited conversation.
                        client?.Dispose();
                        client = null;
                        executor = null;
                        context?.Dispose();
                        context = null;
                        context = weights.CreateContext(parameters);
                        executor = new InteractiveExecutor(context);
                    }
                    client?.Dispose();
                    // The released adapter selects full history on the first
                    // turn and only the last user message later. Supply the
                    // already verified full prompt or exact template suffix.
                    // Preserve the adapter's standard antiprompt output filter;
                    // InteractiveExecutor can emit its own User: marker at EOS.
                    client = executor!.AsChatClient(new WorkerPromptTransform(inferencePrompt));
                    var messages = request.Messages!.Select(message => new ChatMessage(new ChatRole(message.Role), message.Text));
                    var options = new ChatOptions
                    {
                        // InteractiveExecutor subtracts the appended input from
                        // MaxTokens. Compensate only that exact proven suffix;
                        // its remaining generated-token budget stays unchanged.
                        MaxOutputTokens = request.MaxOutputTokens + continuationInputTokens,
                        Temperature = request.Temperature,
                        RawRepresentationFactory = _ => new InferenceParams
                        {
                            OverflowStrategy = ContextOverflowStrategy.ThrowException,
                            SamplingPipeline = new DefaultSamplingPipeline
                            {
                                Grammar = new Grammar(WorkerGeneration.CommandGrammar, "root"),
                                GrammarOptimization = DefaultSamplingPipeline.GrammarOptimizationMode.Basic,
                            },
                        },
                    };
                    StringBuilder text = new();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(115));
                    await foreach (var update in client!.GetStreamingResponseAsync(messages, options, deadline.Token))
                    {
                        if (text.Length + update.Text.Length > ModelProtocol.MaximumOutputCharacters)
                            throw new InvalidDataException("output_limit");
                        text.Append(update.Text);
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    reply = new(ModelProtocol.Version, request.Id, Text: text.ToString());
                    previousMessages = request.Messages;
                    previousOutput = reply.Text;
                    previousPrompt = prompt;
                }
            }
            await ModelProtocol.WriteAsync(output, ModelProtocol.Serialize(reply, ModelProtocol.MaximumResponseBytes), CancellationToken.None);
            if (probeMode || reply.Error is not null) return 0;
        }
        return 0;
    }
    catch (Exception error)
    {
        if (error is TypeInitializationException { InnerException: LLama.Exceptions.RuntimeError }
            or DllNotFoundException or BadImageFormatException)
            failureCode = "runtime_unavailable";
        else if (error is OperationCanceledException)
            failureCode = "inference_timeout";
        Console.Error.WriteLine("worker_" + failureCode + "_" + error.GetType().Name + "_" + error.HResult.ToString("X8")
            + (error.InnerException is { } inner ? "_inner_" + inner.GetType().Name + "_" + inner.HResult.ToString("X8") : ""));
        // Raw native/model exceptions can include paths or input. A closed pipe
        // causes the host to reject the turn and terminate its owned worker.
        if (currentRequestId > 0)
        {
            try
            {
                await ModelProtocol.WriteAsync(output, ModelProtocol.Serialize(new ModelReply(ModelProtocol.Version,
                    currentRequestId, Error: probeMode ? "probe_" + error.GetType().Name + "_" + error.HResult.ToString("X8") : failureCode),
                    ModelProtocol.MaximumResponseBytes), CancellationToken.None);
            }
            catch { }
        }
        return 13;
    }
    finally
    {
        client?.Dispose();
        context?.Dispose();
        weights?.Dispose();
    }
}

static bool ValidChat(ModelRequest request)
{
    if (request.Kind != "chat" || request.Probe is not null || request.Messages is not { Count: > 0 and <= ModelProtocol.MaximumMessages } ||
        request.MaxOutputTokens is < 1 or > ModelProtocol.MaximumOutputTokens || request.Temperature is < 0 or > 1 ||
        !float.IsFinite(request.Temperature)) return false;
    long characters = 0;
    foreach (var message in request.Messages)
    {
        if (message is null || message.Text is null || message.Role is not ("system" or "user" or "assistant")) return false;
        characters += message.Text.Length;
        if (characters > ModelProtocol.MaximumInputCharacters) return false;
    }
    return true;
}

internal sealed class WorkerPromptTransform(string prompt) : IHistoryTransform
{
    public string HistoryToText(ChatHistory history) => prompt;
    public ChatHistory TextToHistory(AuthorRole role, string text) => new([new ChatHistory.Message(role, text)]);
    public IHistoryTransform Clone() => new WorkerPromptTransform(prompt);
}

// Grammar improves formatting, never trust. The host still validates every
// field, ID, request bound, current revision, policy and investigation proposal.
internal static class WorkerGeneration
{
    internal const string CommandGrammar = """
        root ::= children | search | details | summary | propose | done
        children ::= "{" ws "\"kind\"" ws ":" ws "\"children\"" ws "," ws "\"id\"" ws ":" ws guid ws "," ws "\"limit\"" ws ":" ws limit ws "," ws "\"continuation\"" ws ":" ws continuation ws "}"
        search ::= "{" ws "\"kind\"" ws ":" ws "\"search\"" ws "," ws "\"id\"" ws ":" ws guid ws "," ws "\"query\"" ws ":" ws string ws "," ws "\"limit\"" ws ":" ws limit ws "," ws "\"continuation\"" ws ":" ws continuation ws "}"
        details ::= "{" ws "\"kind\"" ws ":" ws "\"details\"" ws "," ws "\"id\"" ws ":" ws guid ws "}"
        summary ::= "{" ws "\"kind\"" ws ":" ws "\"summary\"" ws "," ws "\"ids\"" ws ":" ws "[" ws guid (ws "," ws guid){0,19} ws "]" ws "}"
        propose ::= "{" ws "\"kind\"" ws ":" ws "\"propose\"" ws "," ws "\"proposal\"" ws ":" ws "{" ws "\"schemaVersion\"" ws ":" ws "1" ws "," ws "\"items\"" ws ":" ws "[" ws (item (ws "," ws item){0,19})? ws "]" ws "}" ws "}"
        item ::= "{" ws "\"id\"" ws ":" ws guid ws "," ws "\"action\"" ws ":" ws "\"investigate\"" ws "," ws "\"explanation\"" ws ":" ws string ws "}"
        done ::= "{" ws "\"kind\"" ws ":" ws "\"done\"" ws "," ws "\"message\"" ws ":" ws string ws "}"
        continuation ::= "null" | guid
        limit ::= [1-9] | "1" [0-9] | "20"
        guid ::= "\"" hex4 hex4 "-" hex4 "-" hex4 "-" hex4 "-" hex4 hex4 hex4 "\""
        hex4 ::= [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]
        string ::= "\"" ([^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]))* "\""
        ws ::= [ \t\n\r]*
        """;
}

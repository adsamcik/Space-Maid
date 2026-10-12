using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SpaceMaid.LocalModel;

/// <summary>A text-only .NET AI client backed by the Windows-isolated CPU worker.</summary>
public sealed class IsolatedModelChatClient : IChatClient
{
    private static readonly ChatClientMetadata Metadata = new("Space Maid isolated LLamaSharp CPU");
    private readonly Func<IModelWorkerSession> _startWorker;
    private readonly TimeSpan _turnTimeout;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _workerGate = new();
    private IModelWorkerSession? _worker;
    private ModelFrameReader? _reader;
    private CancellationTokenSource? _workerFailure;
    private bool _disposed;
    private bool _faulted;
    private long _nextId;

    public IsolatedModelChatClient(string workerExecutablePath, string modelPath,
        long memoryLimitBytes = 16L * 1024 * 1024 * 1024)
        : this(() => new SandboxSession(WindowsModelSandbox.Start(workerExecutablePath, modelPath,
            Path.GetDirectoryName(Path.GetFullPath(workerExecutablePath))!, memoryLimitBytes)), TimeSpan.FromSeconds(120))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
    }

    // Stream-only seam for protocol tests. Production has a single fail-closed
    // launcher and never falls back to an ordinary process or local web server.
    internal IsolatedModelChatClient(Func<IModelWorkerSession> startWorker, TimeSpan turnTimeout)
    {
        _startWorker = startWorker;
        _turnTimeout = turnTimeout;
    }

    public string IsolationDescription => "Windows AppContainer (LPAC), no network, read-only model and runtime, Windows registry bootstrap reads, one bounded CPU process";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var input = ReadMessages(messages, options);
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _turn.WaitAsync(queued.Token).ConfigureAwait(false);
        try
        {
            var request = new ModelRequest(ModelProtocol.Version, ++_nextId, "chat", input,
                options?.MaxOutputTokens ?? 768, options?.Temperature ?? 0.1f);
            // Enforce the wire limit before creating a native process or
            // granting read access to runtime/model files.
            var frame = ModelProtocol.Serialize(request, ModelProtocol.MaximumRequestBytes);
            IModelWorkerSession worker;
            CancellationToken failureToken;
            lock (_workerGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_faulted) throw new InvalidOperationException("The isolated model session stopped. Start a new investigation.");
                if (_worker is null)
                {
                    _worker = _startWorker();
                    _reader = new ModelFrameReader(_worker.StandardOutput, ModelProtocol.MaximumResponseBytes);
                    _workerFailure = new CancellationTokenSource();
                    _ = DrainErrorsAsync(_worker.StandardError, _workerFailure, _lifetime.Token);
                }
                worker = _worker;
                failureToken = _workerFailure!.Token;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(queued.Token, failureToken);
            deadline.CancelAfter(_turnTimeout);
            try
            {
                await ModelProtocol.WriteAsync(worker.StandardInput, frame, deadline.Token).ConfigureAwait(false);
                var bytes = await _reader!.ReadAsync(deadline.Token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The isolated model stopped before replying.");
                var reply = ModelProtocol.Deserialize<ModelReply>(bytes);
                if (reply.Version != ModelProtocol.Version || reply.Id != request.Id || reply.Probe is not null)
                    throw new InvalidDataException("The isolated model returned an invalid response.");
                if (reply.Error is not null)
                {
                    if (reply.Text is not null) throw new InvalidDataException("The isolated model returned an invalid response.");
                    throw WorkerError(reply.Error);
                }
                if (reply.Text is null || reply.Text.Length > ModelProtocol.MaximumOutputCharacters)
                    throw new InvalidDataException("The isolated model returned an invalid response.");
                deadline.Token.ThrowIfCancellationRequested();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply.Text))
                {
                    ResponseId = request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CreatedAt = DateTimeOffset.UtcNow,
                    FinishReason = ChatFinishReason.Stop,
                };
            }
            catch (OperationCanceledException) when (!queued.IsCancellationRequested)
            {
                throw new TimeoutException(failureToken.IsCancellationRequested
                    ? "The isolated model exceeded its diagnostic limit. Start a new investigation."
                    : "The isolated model took too long. Try a smaller model or scope.");
            }
        }
        catch
        {
            StopWorker(faulted: true);
            throw;
        }
        finally { _turn.Release(); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
        {
            ResponseId = response.ResponseId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null : serviceType == typeof(ChatClientMetadata) ? Metadata :
            serviceType == typeof(IChatClient) || serviceType == typeof(IsolatedModelChatClient) ? this : null;
    }

    public void Dispose()
    {
        lock (_workerGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        StopWorker(faulted: false);
    }

    private void StopWorker(bool faulted)
    {
        IModelWorkerSession? worker;
        lock (_workerGate)
        {
            _faulted |= faulted;
            worker = _worker;
            _worker = null;
        }
        worker?.Dispose();
    }

    private static IReadOnlyList<ModelMessage> ReadMessages(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (options?.RawRepresentationFactory is not null || options?.Tools?.Count > 0)
            throw new ArgumentException("The isolated model accepts text only. Native options and executable tools are unavailable.", nameof(options));
        if (options?.MaxOutputTokens is < 1 or > ModelProtocol.MaximumOutputTokens ||
            options?.Temperature is < 0 or > 1 || (options?.Temperature is float temperature && !float.IsFinite(temperature)))
            throw new ArgumentOutOfRangeException(nameof(options), "Model output limits or temperature are out of range.");

        List<ModelMessage> result = [];
        int characters = 0;
        if (!string.IsNullOrWhiteSpace(options?.Instructions)) Add("system", options.Instructions);
        foreach (var message in messages)
        {
            if (message is null || message.Contents.Any(content => content is not TextContent))
                throw new ArgumentException("The isolated model accepts text messages only.", nameof(messages));
            var role = message.Role == ChatRole.System ? "system" : message.Role == ChatRole.User ? "user" :
                message.Role == ChatRole.Assistant ? "assistant" : throw new ArgumentException("Unsupported model message role.", nameof(messages));
            Add(role, message.Text);
        }
        if (result.Count == 0) throw new ArgumentException("The isolated model needs at least one message.", nameof(messages));
        return result;

        void Add(string role, string text)
        {
            characters = checked(characters + text.Length);
            if (characters > ModelProtocol.MaximumInputCharacters || result.Count >= ModelProtocol.MaximumMessages)
                throw new ArgumentException("The investigation exceeds the model input limit. Use a smaller scope.", nameof(messages));
            result.Add(new(role, text));
        }
    }

    private static Exception WorkerError(string error) => error switch
    {
        "inference_timeout" => new TimeoutException("The local model took too long. Try a smaller model or a more focused request."),
        "runtime_unavailable" => new InvalidOperationException("The bundled CPU runtime could not be loaded. Repair the app and its Microsoft Visual C++ runtime."),
        "model_load_failed" => new InvalidOperationException("The local model could not be loaded. Choose a compatible GGUF model that fits in memory."),
        "template_unsupported" => new InvalidOperationException("This model's embedded chat template is unsupported. Choose another GGUF instruction model."),
        "context_limit" => new InvalidOperationException("The investigation exceeds this model's context. Choose a smaller folder or a more focused request."),
        "inference_failed" => new InvalidOperationException("The local model could not finish this turn. Try another compatible GGUF instruction model."),
        _ => new InvalidDataException("The isolated model returned an invalid response."),
    };

    private static async Task DrainErrorsAsync(Stream stream, CancellationTokenSource failure, CancellationToken token)
    {
        // Native library diagnostics are discarded. Neither prompts nor model
        // contents are written to app logs, and a flood cannot retain memory.
        byte[] buffer = new byte[4096];
        long bytes = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) return;
                bytes += read;
                if (bytes > 8 * 1024 * 1024) { failure.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { failure.Cancel(); }
    }

    private sealed class SandboxSession(WindowsModelSandbox sandbox) : IModelWorkerSession
    {
        public Stream StandardInput => sandbox.StandardInput;
        public Stream StandardOutput => sandbox.StandardOutput;
        public Stream StandardError => sandbox.StandardError;
        public void Dispose() => sandbox.Dispose();
    }
}

internal interface IModelWorkerSession : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
}

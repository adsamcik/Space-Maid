using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SpaceMaid.App.Extensions;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Extensions.Contracts;
using SpaceMaid.LocalModel;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AssistantInvestigationTests
{
    [Fact]
    public async Task StandardClientBrowsesIssuedMetadataAndReturnsHostBoundInvestigations()
    {
        var scan = Fixture();
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope", []);
        var folder = Assert.Single((await evidence.ChildrenAsync(evidence.Scope.Root.Id, 20, null, default)).Items, x => x.Name == "output");
        using var client = new ScriptedChatClient((messages, turn, _) => Task.FromResult(turn == 1
            ? JsonSerializer.Serialize(new { kind = "summary", ids = new[] { folder.Id.Value } })
            : Proposal(folder.Id)));
        var result = await new AssistantInvestigation(client, evidence).RunAsync("Investigate generated output", null, default);
        Assert.Equal(folder.Id, Assert.Single(result.Suggestions).Entry.Id);
        Assert.Equal(EvidenceReviewDisposition.ManualInvestigation, result.Suggestions[0].Entry.ReviewPolicy.Disposition);
        Assert.Equal(1234, result.Summary.Totals.LogicalBytes);
        Assert.Equal(scan.ScannedAtUtc, result.Summary.Coverage.ScannedAtUtc);
        Assert.Contains(client.Calls[1], message => message.Text.Contains("1234"));
        using var measured = JsonDocument.Parse(client.Calls[1].Last().Text);
        var facts = measured.RootElement.GetProperty("toolResult");
        Assert.Equal(folder.Id.Value, facts.GetProperty("roots")[0].GetGuid());
        Assert.Equal(1234, facts.GetProperty("totals").GetProperty("logicalBytes").GetInt64());
        Assert.False(facts.TryGetProperty("text", out _)); // Display prose would duplicate these measured facts.
        Assert.All(client.Options, options => { Assert.Empty(options.Tools ?? []); Assert.Equal(1024, options.MaxOutputTokens); });
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("invented-id")]
    [InlineData("path")]
    [InlineData("duplicate")]
    public async Task UntrustedAuthorityAndMalformedCommandsPublishNoSuggestions(string attack)
    {
        var scan = Fixture();
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope", []);
        var folder = Assert.Single((await evidence.ChildrenAsync(evidence.Scope.Root.Id, 20, null, default)).Items, x => x.Name == "output");
        string command = attack switch
        {
            "delete" => Proposal(folder.Id, "delete"),
            "invented-id" => Proposal(new(Guid.NewGuid())),
            "path" => JsonSerializer.Serialize(new { kind = "details", id = folder.Id.Value, path = @"C:\Outside\private.txt" }),
            _ => "{\"kind\":\"done\",\"kind\":\"delete\",\"message\":\"safe\"}"
        };
        using var client = new ScriptedChatClient((_, _, _) => Task.FromResult(command));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AssistantInvestigation(client, evidence).RunAsync("Investigate", null, default));
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task ExcludedNamesAndGlobalWarningPathsNeverReachModelContext()
    {
        var scan = Fixture(warning: @"Cannot inspect C:\Outside\secret.txt");
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope", [@"C:\Scope\private-do-not-share"]);
        using var client = new ScriptedChatClient((_, _, _) => Task.FromResult("{\"kind\":\"done\",\"message\":\"No conclusion\"}"));
        var result = await new AssistantInvestigation(client, evidence).RunAsync("Investigate", null, default);
        string context = string.Join("\n", client.Calls.Single().Select(x => x.Text));
        Assert.DoesNotContain("private-do-not-share", context); Assert.DoesNotContain("secret.txt", context);
        Assert.False(result.Summary.Coverage.Complete); Assert.Equal(1, result.Summary.Coverage.WarningCount);
        Assert.Equal("No conclusion", result.UntrustedNote); Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task SnapshotReplacementDuringInferenceRejectsAPreviouslyValidProposal()
    {
        ScanSnapshot? scan = Fixture();
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope", []);
        var folder = Assert.Single((await evidence.ChildrenAsync(evidence.Scope.Root.Id, 20, null, default)).Items, x => x.Name == "output");
        using var client = new ScriptedChatClient((_, _, _) => { scan = Fixture(); return Task.FromResult(Proposal(folder.Id)); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AssistantInvestigation(client, evidence).RunAsync("Investigate", null, default));
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task EndlessMetadataQueriesStopAtTheTurnBudget()
    {
        var scan = Fixture();
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope", []);
        using var client = new ScriptedChatClient((_, _, _) => Task.FromResult(JsonSerializer.Serialize(new { kind = "details", id = evidence.Scope.Root.Id.Value })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AssistantInvestigation(client, evidence).RunAsync("Investigate", null, default));
        Assert.Equal(AssistantInvestigation.MaximumTurns, client.Calls.Count);
    }

    [Fact]
    public async Task CancellationDuringInferencePublishesNothing()
    {
        var scan = Fixture();
        using var evidence = new InventoryEvidenceSession(() => scan, () => false, @"C:\Scope", []);
        using var cancel = new CancellationTokenSource();
        using var client = new ScriptedChatClient(async (_, _, token) => { cancel.Cancel(); await Task.Delay(1000, token); return "{}"; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AssistantInvestigation(client, evidence).RunAsync("Investigate", null, cancel.Token));
        Assert.Single(client.Calls);
    }

    internal static string Proposal(EvidenceId id, string action = "investigate") => JsonSerializer.Serialize(new
    { kind = "propose", proposal = new { schemaVersion = 1, items = new[] { new { id = id.Value, action, explanation = "Check whether the generated output is still needed; preserve custom files." } } } });

    private static ScanSnapshot Fixture(string? warning = null)
    {
        var builder = new ScanBuilder { AllStreams = true, CompleteVolumeLinks = true, AllocationData = true };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int output = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(output, new(1), "output");
        int file = builder.AddFile(new(new(3), FileAttributes.Normal, 1234, 4096, 0)); builder.AddLink(file, new(2), "generated.dll");
        int excluded = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(excluded, new(1), "private-do-not-share");
        if (warning is not null) builder.Warn(warning);
        return builder.Build(@"C:\Scope", "fixture", TimeSpan.Zero, 0, default);
    }
}

internal sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, int, CancellationToken, Task<string>> respond) : IChatClient
{
    internal List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
    internal List<ChatOptions> Options { get; } = [];
    internal bool Disposed { get; private set; }
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copy = messages.ToArray(); Calls.Add(copy); Options.Add(options ?? new());
        return new(new ChatMessage(ChatRole.Assistant, await respond(copy, Calls.Count, cancellationToken)));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => Disposed = true;
}

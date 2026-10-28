using System.IO;
using System.Text;
using System.Text.Json;
using SpaceMaid.App.AgentAccess;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class AgentAccessTests
{
    [Fact]
    public async Task ProjectionOmitsNamesPathsWarningsAndAllExtensionProse()
    {
        var (scan, findings) = Fixture(warning: true);
        using var session = await Open(scan, findings);
        string output = JsonSerializer.Serialize(new { session.Overview, session.Groups }, AgentProtocol.Json);
        foreach (string secret in new[] { "PRIVATE", "C:\\", "password", "financial", "untrusted-provider" })
            Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, session.Overview.Coverage.WarningCount);
        Assert.False(session.Overview.Coverage.Complete);
        Assert.All(session.Groups, group => Assert.False(group.CanSuggestReview));
        Assert.Contains(session.Groups, group => group.ToolClass == "Other tools");
    }

    [Fact]
    public async Task ExclusionsRemoveTheirDataAndAnyContainingCleanupTarget()
    {
        var (scan, findings) = Fixture();
        using var session = await Open(scan, findings, exclusions: [@"C:\PRIVATE\build\nested"]);
        Assert.Equal(7, session.Overview.LogicalBytes);
        Assert.Equal(2, session.Overview.FileNames);
        Assert.DoesNotContain(session.Groups, group => group.StorageClass == "Build output");
        Assert.DoesNotContain("nested", JsonSerializer.Serialize(session.Groups));
    }

    [Fact]
    public async Task GroupTotalsCollapseNestedTargetsAndFlagOverlapAcrossGroups()
    {
        var (scan, findings) = Fixture();
        using var session = await Open(scan, findings);
        var build = Assert.Single(session.Groups, group => group.StorageClass == "Build output");
        Assert.Equal(2, build.Locations); Assert.Equal(12, build.LogicalBytes);
        Assert.Null(build.AllocatedBytes);
        Assert.Contains("overlap", session.Overview.SizeMeaning);
        Assert.Equal(14, session.Overview.LogicalBytes);
    }

    [Fact]
    public async Task AccessLevelsCannotBeEscalatedThroughArgumentsOrUnadvertisedTools()
    {
        var (scan, findings) = Fixture();
        using var summary = await Open(scan, findings, AgentAccessLevel.Summary);
        Assert.Empty(summary.Groups);
        Assert.Throws<InvalidDataException>(() => AgentProtocol.Execute(summary, "get_groups", Args("{}")));
        Assert.Throws<InvalidDataException>(() => AgentProtocol.Execute(summary, "get_summary", Args("{\"path\":\"C:\\\\PRIVATE\"}")));
        using var groups = await Open(scan, findings, AgentAccessLevel.Groups);
        Assert.NotEmpty(groups.Groups);
        Assert.Throws<InvalidDataException>(() => groups.Suggest([groups.Groups[0].Id], "review"));
        string tools = JsonSerializer.Serialize(AgentProtocol.Tools(AgentAccessLevel.Summary));
        Assert.Contains("get_summary", tools); Assert.DoesNotContain("get_groups", tools);
        Assert.DoesNotContain("suggest_review", tools);
    }

    [Fact]
    public async Task SuggestionsUseOnlyCurrentIssuedReviewableIdsAndNeverCreateTargets()
    {
        var (scan, findings) = Fixture();
        using var session = await Open(scan, findings);
        var build = session.Groups.Single(group => group.StorageClass == "Build output");
        var blocked = session.Groups.Single(group => group.Policy == "Protected");
        Assert.Throws<InvalidDataException>(() => session.Suggest([build.Id, blocked.Id], "review"));
        Assert.Throws<InvalidDataException>(() => session.Suggest([build.Id, build.Id], "review"));
        Assert.Throws<InvalidDataException>(() => session.Suggest([Guid.NewGuid()], "review"));
        using var other = await Open(scan, findings);
        Assert.Throws<InvalidDataException>(() => other.Suggest([build.Id], "review"));
        Assert.Null(session.Suggestion);
        session.Suggest([build.Id], "Please evaluate rebuild cost.");
        Assert.Throws<InvalidDataException>(() => session.Suggest([build.Id], "replace"));
        var review = session.TakeReview();
        Assert.Equal(2, review.Count); Assert.All(review, item => Assert.Contains(item, findings));
        Assert.Null(session.Suggestion);
    }

    [Fact]
    public async Task ScanReplacementStalenessAndDisposalRevokeTheSessionPermanently()
    {
        var (scan, findings) = Fixture(); bool current = true;
        using var session = await AgentAggregateSession.CreateAsync(scan, findings, scan.RootPath, [],
            AgentAccessLevel.SuggestReview, true, () => current, default);
        var id = session.Groups.First(group => group.CanSuggestReview).Id;
        session.Suggest([id], "review"); current = false;
        Assert.Throws<InvalidOperationException>(session.EnsureCurrent);
        current = true;
        Assert.Throws<InvalidOperationException>(() => session.TakeReview());
        Assert.Null(session.Suggestion);
        session.Dispose(); Assert.Throws<InvalidOperationException>(session.EnsureCurrent);
    }

    [Theory]
    [InlineData("{\"path\":\"PRIVATE\"}")]
    [InlineData("{\"level\":\"SuggestReview\"}")]
    [InlineData("{\"anything\":{\"shell\":\"delete\"}}")]
    public async Task McpRejectsUnsupportedFieldsWithoutReflectingInput(string args)
    {
        var (scan, findings) = Fixture(); using var session = await Open(scan, findings);
        var protocol = new AgentProtocol((name, input) => Task.FromResult(AgentProtocol.Execute(session, name, input)), session.Level);
        await Initialize(protocol);
        string reply = (await protocol.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_summary\",\"arguments\":" + args + "}}"))!;
        Assert.True(Args(reply).GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.DoesNotContain("PRIVATE", reply); Assert.DoesNotContain("delete", reply);
    }

    [Fact]
    public async Task McpLifecycleDuplicateFieldsAndHostErrorsAreBoundedAndSanitized()
    {
        var protocol = new AgentProtocol((_, _) => throw new InvalidOperationException(@"C:\PRIVATE\password"), AgentAccessLevel.Groups);
        string call = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_summary\"}}";
        Assert.Contains("-32601", (await protocol.HandleAsync(call))!);
        await Initialize(protocol);
        Assert.Contains("isError\":true", (await protocol.HandleAsync(call))!);
        Assert.DoesNotContain("PRIVATE", (await protocol.HandleAsync(call))!);
        Assert.Contains("-32602", (await protocol.HandleAsync(call.Replace("\"id\":3", "\"id\":3,\"id\":4")))!);
        Assert.Contains("-32700", (await protocol.HandleAsync("bad json"))!);
        Assert.Null(await protocol.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{}}"));
    }

    [Fact]
    public async Task FrameReaderRejectsOversizedAndTruncatedRequests()
    {
        using var tooLarge = AgentConnection.Reader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 16385) + "\n")));
        await Assert.ThrowsAsync<IOException>(() => AgentConnection.ReadLineAsync(tooLarge, default));
        using var partial = AgentConnection.Reader(new MemoryStream(Encoding.UTF8.GetBytes("partial")));
        await Assert.ThrowsAsync<IOException>(() => AgentConnection.ReadLineAsync(partial, default));
    }

    internal static async Task Initialize(AgentProtocol protocol)
    {
        string reply = (await protocol.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}"))!;
        Assert.Contains("protocolVersion", reply);
        Assert.Null(await protocol.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
    }
    internal static JsonElement Args(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static Task<AgentAggregateSession> Open(ScanSnapshot scan, Opportunity[] findings,
        AgentAccessLevel level = AgentAccessLevel.SuggestReview, string[]? exclusions = null) =>
        AgentAggregateSession.CreateAsync(scan, findings, scan.RootPath, exclusions ?? [], level, true, () => true, default);

    private static (ScanSnapshot Scan, Opportunity[] Findings) Fixture(bool warning = false)
    {
        var builder = new ScanBuilder { AllocationData = false };
        builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
        int build = builder.AddFile(new(new(2), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(build, new(1), "build");
        int nested = builder.AddFile(new(new(3), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(nested, new(2), "nested");
        int protectedStore = builder.AddFile(new(new(4), FileAttributes.Directory, 0, 0, 0)); builder.AddLink(protectedStore, new(1), "passwords");
        int a = builder.AddFile(new(new(5), FileAttributes.Normal, 5, 4096, 0)); builder.AddLink(a, new(2), "financial.txt");
        int b = builder.AddFile(new(new(6), FileAttributes.Normal, 7, 4096, 0)); builder.AddLink(b, new(3), "PRIVATE.txt");
        int c = builder.AddFile(new(new(7), FileAttributes.Normal, 2, 4096, 0)); builder.AddLink(c, new(4), "password.txt");
        if (warning) builder.Warn(@"PRIVATE C:\financial password");
        var scan = builder.Build(@"C:\PRIVATE", "PRIVATE backend", TimeSpan.Zero, 0, default);
        Opportunity Item(int directory, string provider = "space-maid.developer") =>
            new(directory, "PRIVATE title", "PRIVATE category", scan.EntryPath(scan.PrimaryLink(directory))!, 0, 0,
                "PRIVATE reason", "PRIVATE caution", "PRIVATE action", false)
            { ProviderId = provider, RuleId = "PRIVATE-rule", IsBuildOutput = directory != protectedStore,
                CleanupBlockReason = directory == protectedStore ? "PRIVATE blocked" : "" };
        return (scan, [Item(build), Item(nested), Item(protectedStore, "untrusted-provider")]);
    }
}

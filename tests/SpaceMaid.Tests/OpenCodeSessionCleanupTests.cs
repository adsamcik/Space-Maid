using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SpaceMaid.Extensions.Agents;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class OpenCodeSessionCleanupTests
{
    private static readonly long Created = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
    private static readonly long Updated = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds();
    private static readonly OpenCodeConnection Connection = new("");
    private static readonly OpenCodeServer Server = new("1.2.27", @"C:\owned\opencode", @"C:\owned\project");
    private static readonly OpenCodeSession Selected = new("ses_abc123", "Owned session", Server.Directory, "project123", Created, Updated, null, "idle", true);

    [Fact]
    public async Task PreviewDoesNotMutateAndConfirmedCleanupUsesTheExactLocalSession()
    {
        bool deleted = false;
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) { deleted = true; return Json("true"); }
            if (deleted && request.RequestUri!.AbsolutePath == "/session/" + Selected.Id) return new(HttpStatusCode.NotFound);
            return Standard(request);
        });
        using var client = new HttpClient(handler);
        var snapshot = await OpenCodeSessions.InspectAsync(client, Connection, 30, default);
        Assert.True(Assert.Single(snapshot.Sessions).CanDelete);
        var plan = await OpenCodeSessions.PrepareAsync(client, Connection, snapshot, Selected, 30, default);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, plan, default);
        Assert.True(result.Attempted); Assert.True(result.Deleted);
        var deletion = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal(OpenCodeSessions.LocalEndpoint + "/session/" + Selected.Id + "?directory=" + Uri.EscapeDataString(Selected.Directory), deletion.Url);
        Assert.All(handler.Requests, request => Assert.StartsWith(OpenCodeSessions.LocalEndpoint + "/", request.Url));
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("abort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("retry")]
    public async Task ActiveOrRetryingSessionsAreKeptAtInspectionAndExecution(string status)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/session/status"
            ? Json(JsonSerializer.Serialize(new Dictionary<string, object> { [Selected.Id] = new { type = status } })) : Standard(request));
        using var client = new HttpClient(handler);
        var snapshot = await OpenCodeSessions.InspectAsync(client, Connection, 30, default);
        Assert.False(Assert.Single(snapshot.Sessions).CanDelete);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.False(result.Attempted); Assert.False(result.Deleted);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("updated")]
    [InlineData("directory")]
    [InlineData("project")]
    [InlineData("parent")]
    [InlineData("storage")]
    [InlineData("version")]
    [InlineData("children")]
    [InlineData("unknown-status")]
    public async Task ChangedIdentityScopeChildrenOrUnknownActivityBlockConfirmedDeletion(string change)
    {
        using var handler = new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (change == "storage" && path == "/path") return Json("{\"state\":\"C:\\\\other\",\"directory\":\"C:\\\\owned\\\\project\"}");
            if (change == "version" && path == "/global/health") return Json("{\"healthy\":true,\"version\":\"1.2.28\"}");
            if (change == "children" && path.EndsWith("/children", StringComparison.Ordinal)) return Json("[{}]");
            if (change == "unknown-status" && path == "/session/status") return Json("{\"ses_abc123\":{\"type\":\"unknown\"}}");
            if (path == "/session/" + Selected.Id)
                return Json(Session(Selected with
                {
                    Title = change == "title" ? "Changed" : Selected.Title,
                    Updated = change == "updated" ? Updated + 1 : Updated,
                    Directory = change == "directory" ? @"C:\other" : Selected.Directory,
                    ProjectId = change == "project" ? "other" : Selected.ProjectId,
                    ParentId = change == "parent" ? "ses_parent" : null
                }));
            return Standard(request);
        });
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.False(result.Attempted); Assert.False(result.Deleted);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ActivityStartingDuringTheLeafCheckBlocksDeletion()
    {
        int statusReads = 0;
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/session/status" && ++statusReads == 2
            ? Json("{\"ses_abc123\":{\"type\":\"busy\"}}") : Standard(request));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.Equal(2, statusReads); Assert.False(result.Attempted);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData("share")]
    [InlineData("workspaceID")]
    public async Task SharedAndWorkspaceManagedSessionsAreKeptIncludingFreshChanges(string property)
    {
        string body = Session(Selected)[..^1] + ",\"" + property + "\":"
            + (property == "share" ? "{\"url\":\"https://example.invalid/owned-share\"}" : "\"workspace-owned\"") + "}";
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/session" ? Json("[" + body + "]")
            : request.RequestUri!.AbsolutePath == "/session/" + Selected.Id ? Json(body) : Standard(request));
        using var client = new HttpClient(handler);
        var snapshot = await OpenCodeSessions.InspectAsync(client, Connection, 30, default);
        Assert.False(Assert.Single(snapshot.Sessions).CanDelete);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.False(result.Attempted); Assert.False(result.Deleted);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ChangedLocalProjectCannotHideChildSessionsFromTheCleanupCheck()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/project/current"
            ? Json("{\"id\":\"global\"}") : Standard(request));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.False(result.Attempted); Assert.Contains("project identity changed", result.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ChildSessionsStayWithTheirParentEvenWhenOldAndIdle()
    {
        var child = Selected with { ParentId = "ses_parent" };
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/session" ? Json("[" + Session(child) + "]")
            : request.RequestUri!.AbsolutePath == "/session/" + Selected.Id ? Json(Session(child)) : Standard(request));
        using var client = new HttpClient(handler);
        var snapshot = await OpenCodeSessions.InspectAsync(client, Connection, 30, default);
        Assert.False(Assert.Single(snapshot.Sessions).CanDelete);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, child, 30), default);
        Assert.False(result.Attempted); Assert.Contains("parent", result.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("not JSON")]
    public async Task UnconfirmedMutationResultsAreUncertainAndNeverRetried(string response)
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Delete ? Json(response) : Standard(request));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.True(result.Attempted); Assert.False(result.Deleted);
        Assert.Contains("may have reached", result.Message);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task SessionStillPresentAfterDeleteIsNotReportedAsRemoved()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Delete ? Json("true") : Standard(request));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.True(result.Attempted); Assert.False(result.Deleted); Assert.Contains("absence was not confirmed", result.Message);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task AuthenticationUsesOnlyAnInMemoryHeaderAndSecretsAreNotInConnectionText()
    {
        var connection = new OpenCodeConnection("owned-test-password");
        using var handler = new Handler(Standard); using var client = new HttpClient(handler);
        await OpenCodeSessions.InspectAsync(client, connection, 30, default);
        Assert.DoesNotContain("password", connection.ToString(), StringComparison.Ordinal);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:owned-test-password")), request.Authorization);
            Assert.DoesNotContain("password", request.Url, StringComparison.Ordinal);
        });
        var error = new HttpRequestException("fixture", null, HttpStatusCode.Unauthorized);
        Assert.Contains("authentication", OpenCodeSessions.SafeError(error));
    }

    [Fact]
    public void InvalidOrOversizedListsAndDatesCannotBeUsedAsCleanupEvidence()
    {
        var statuses = OpenCodeSessions.ParseStatus(Encoding.UTF8.GetBytes("{}"));
        Assert.Empty(statuses);
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseStatus(Encoding.UTF8.GetBytes("[]")));
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseStatus(Encoding.UTF8.GetBytes("{\"ses_a\":{\"type\":\"idle\"},\"ses_a\":{\"type\":\"busy\"}}")));
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseStatus(Encoding.UTF8.GetBytes("{\"ses_a\":{\"type\":\"busy\",\"type\":\"idle\"}}")));
        string duplicate = "[" + Session(Selected) + "," + Session(Selected) + "]";
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseSessions(Encoding.UTF8.GetBytes(duplicate), statuses, 30, DateTimeOffset.UtcNow));
        string many = "[" + string.Join(',', Enumerable.Range(0, OpenCodeSessions.MaximumSessions + 1).Select(index => Session(Selected with { Id = "ses_" + index }))) + "]";
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseSessions(Encoding.UTF8.GetBytes(many), statuses, 30, DateTimeOffset.UtcNow));
        var recent = Assert.Single(OpenCodeSessions.ParseSessions(Encoding.UTF8.GetBytes("[" + Session(Selected with { Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "]"), statuses, 30, DateTimeOffset.UtcNow));
        Assert.False(recent.CanDelete);
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseSessions(Encoding.UTF8.GetBytes("[" + Session(Selected with { Updated = 0 }) + "]"), statuses, 30, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseSessions(Encoding.UTF8.GetBytes("[" + Session(Selected with { Id = "ses_bad/path" }) + "]"), statuses, 30, DateTimeOffset.UtcNow));
        string ambiguous = "[" + Session(Selected)[..^1] + ",\"share\":{\"url\":\"https://example.invalid/shared\"},\"share\":null}]";
        Assert.Throws<InvalidDataException>(() => OpenCodeSessions.ParseSessions(Encoding.UTF8.GetBytes(ambiguous), statuses, 30, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task WholeBodyDeadlineAndResponseBoundsFailClosed()
    {
        using var oversized = new Handler(_ =>
        {
            var response = Json("{}"); response.Content.Headers.ContentLength = OpenCodeSessions.MaximumResponseBytes + 1L; return response;
        });
        using var oversizedClient = new HttpClient(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenCodeSessions.InspectAsync(oversizedClient, Connection, 30, default));
        using var stalled = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var stalledClient = new HttpClient(stalled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenCodeSessions.InspectAsync(stalledClient, Connection, 30, default, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task UnsupportedServerVersionAndCancellationNeverSendDeletion()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/global/health"
            ? Json("{\"healthy\":true,\"version\":\"2.0.0\"}") : Standard(request));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), default);
        Assert.False(result.Attempted);
        result = await OpenCodeSessions.ExecuteAsync(client, Connection, new(Server, Selected, 30), new(true));
        Assert.False(result.Attempted);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenCodeSessions.InspectAsync(client, Connection, 0, default));
    }

    private static HttpResponseMessage Standard(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/global/health" => Json(JsonSerializer.Serialize(new { healthy = true, version = Server.Version })),
        "/path" => Json(JsonSerializer.Serialize(new { state = Server.StateDirectory, directory = Server.Directory })),
        "/project/current" => Json(JsonSerializer.Serialize(new { id = Selected.ProjectId })),
        "/session" => Json("[" + Session(Selected) + "]"),
        "/session/status" => Json("{}"),
        var path when path.EndsWith("/children", StringComparison.Ordinal) => Json("[]"),
        _ => Json(Session(Selected))
    };
    private static string Session(OpenCodeSession session) => JsonSerializer.Serialize(new
    {
        id = session.Id, title = session.Title, directory = session.Directory, projectID = session.ProjectId,
        parentID = session.ParentId, time = new { created = session.Created, updated = session.Updated }
    });
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed record Captured(HttpMethod Method, string Url, string? Authorization);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Captured> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

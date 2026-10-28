using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SpaceMaid.App;
using Velopack;
using Velopack.Locators;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class GitHubUpdateTests
{
    private const string Repository = "owner/Space-Maid";
    private const string DownloadRoot = "https://github.com/owner/Space-Maid/releases/download/v1.1.0/";

    [Theory]
    [InlineData("http://github.com/owner/Space-Maid/releases/download/v1.1.0/package.nupkg")]
    [InlineData("https://github.com/other/repo/releases/download/v1.1.0/package.nupkg")]
    [InlineData("https://github.com.evil.test/owner/Space-Maid/releases/download/v1.1.0/package.nupkg")]
    [InlineData("https://api.github.com/repos/owner/Space-Maid/releases-evil")]
    [InlineData("https://release-assets.githubusercontent.com/package.nupkg")]
    public async Task UntrustedInitialUrlsNeverReachTheNetwork(string url)
    {
        var handler = new ResponseHandler(_ => new(HttpStatusCode.OK));
        var downloader = new GitHubUpdateDownloader(Repository, () => handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadBytes(url, null, 1));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/package.nupkg")]
    [InlineData("https://evil.test/package.nupkg")]
    [InlineData("https://release-assets.githubusercontent.com.evil.test/package.nupkg")]
    [InlineData("https://github.com/other/repo/releases/download/v1.1.0/package.nupkg")]
    public async Task UnsafeRedirectsStopBeforeSendingTheRedirectedRequest(string location)
    {
        var handler = new ResponseHandler(_ => Redirect(location));
        var downloader = new GitHubUpdateDownloader(Repository, () => handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadBytes(DownloadRoot + "package.nupkg", null, 1));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task GitHubCdnRedirectDownloadsExactBytesAndDropsCredentials()
    {
        using var fixture = new TestDirectory();
        byte[] bytes = "fixture update bytes"u8.ToArray();
        var handler = new ResponseHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return request.RequestUri!.Host == "github.com"
                ? Redirect("https://release-assets.githubusercontent.com/owned/package.nupkg?signature=fixture")
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var downloader = new GitHubUpdateDownloader(Repository, () => handler);
        string path = Path.Join(fixture.Path, "package.nupkg");
        await downloader.DownloadFile(DownloadRoot + "package.nupkg", path, _ => { },
            new Dictionary<string, string> { ["Authorization"] = "Bearer do-not-forward" }, 1);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task RedirectLoopsMetadataAndOversizedDownloadsAreBounded()
    {
        var redirects = new ResponseHandler(_ => Redirect(DownloadRoot + "package.nupkg"));
        var downloader = new GitHubUpdateDownloader(Repository, () => redirects);
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadBytes(DownloadRoot + "package.nupkg", null, 1));
        Assert.Equal(6, redirects.Requests);
        var largeMetadata = new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]) });
        await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubUpdateDownloader(Repository, () => largeMetadata)
            .DownloadBytes("https://api.github.com/repos/owner/Space-Maid/releases", null, 1));
        using var fixture = new TestDirectory();
        var oversized = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = 1_073_741_825;
            return response;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubUpdateDownloader(Repository, () => oversized)
            .DownloadFile(DownloadRoot + "package.nupkg", Path.Join(fixture.Path, "package.nupkg"), _ => { }, null, 1));
    }

    [Theory]
    [InlineData("v1.1.0", false)]
    [InlineData("v1.0.0", true)]
    public async Task RealSourceBindsTheFeedPackageToItsRepositoryAndVersionTag(string tag, bool rejected)
    {
        using var fixture = new TestDirectory();
        var target = UpdateTests.Target();
        string root = $"https://github.com/{Repository}/releases/download/{tag}/";
        string releases = JsonSerializer.Serialize(new[] { new
        {
            prerelease = false, published_at = "2026-10-28T00:00:00Z", assets = new[]
            {
                new { name = "releases.win.json", browser_download_url = root + "releases.win.json", updated_at = "2026-10-27T00:00:00Z" },
                new { name = target.FileName, browser_download_url = root + target.FileName, updated_at = "2026-10-28T00:30:00Z" }
            }
        } });
        var handler = new ResponseHandler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "api.github.com"
                ? releases : JsonSerializer.Serialize(new { Assets = new[] { new
                {
                    target.PackageId, Version = target.Version.ToString(), Type = "Full", target.FileName, target.SHA256, target.Size
                } } }))
        });
        var source = new GitHubUpdateSource(Repository, false, new GitHubUpdateDownloader(Repository, () => handler));
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("SpaceMaid", "1.0.0", fixture.Path));
        if (rejected) await Assert.ThrowsAsync<InvalidDataException>(() => manager.CheckForUpdatesAsync());
        else
        {
            var update = await manager.CheckForUpdatesAsync();
            Assert.Equal("1.1.0", update!.TargetFullRelease.Version.ToString());
            Assert.Equal(target.SHA256, update.TargetFullRelease.SHA256);
            Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:30:00Z"), GitHubUpdateSource.AutomaticDownloadAfter(update.TargetFullRelease));
        }
        Assert.Equal(2, handler.Requests);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(respond(request)); }
    }
}

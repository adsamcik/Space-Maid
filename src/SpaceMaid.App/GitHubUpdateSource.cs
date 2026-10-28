using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SpaceMaid.App;

// Keep Velopack's release selection and installer, with a repository-bound transport.
internal sealed class GitHubUpdateSource(string repository, bool prerelease, IFileDownloader? downloader = null)
    : GithubSource(AppUpdates.RepositoryUrl(repository) ?? throw new ArgumentException("Invalid repository.", nameof(repository)),
        null, prerelease, downloader ?? new GitHubUpdateDownloader(repository))
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // Velopack does not retain package timestamps; keep the GitHub API's
        // dates alongside each release so a new upload restarts the safety wait.
        string response = await Downloader.DownloadString(
            $"https://api.github.com/repos{RepoUri.AbsolutePath}/releases?per_page=10&page=1",
            GetRequestHeaders("application/vnd.github+json")).ConfigureAwait(false);
        using var json = JsonDocument.Parse(response);
        var releases = new List<GithubRelease>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var release = item.Deserialize<GithubRelease>() ?? throw new InvalidDataException("Invalid GitHub release.");
            if ((!includePrereleases && release.Prerelease) || (item.TryGetProperty("draft", out var draft) && draft.GetBoolean())) continue;
            var modified = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            foreach (var asset in item.GetProperty("assets").EnumerateArray())
                modified.Add(asset.GetProperty("name").GetString()!, ReadDate(asset, "updated_at"));
            releases.Add(new TimedRelease(release, ReadDate(item, "published_at"), modified));
        }
        return releases.OrderByDescending(release => release.PublishedAt).ToArray();
    }

    private static DateTimeOffset? ReadDate(JsonElement item, string property) => item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date) ? date.ToUniversalTime() : null;

    internal static DateTimeOffset? AutomaticDownloadAfter(VelopackAsset asset)
    {
        if (asset is not GitBaseAsset { Release: TimedRelease release } || release.PublishedUtc is not { } published
            || !release.PackageModifiedUtc.TryGetValue(asset.FileName, out var changed) || changed is not { } modified) return null;
        var latest = published > modified ? published : modified;
        return latest <= DateTimeOffset.MaxValue.AddHours(-24) ? latest.AddHours(24) : null;
    }

    internal sealed class TimedRelease : GithubRelease
    {
        internal DateTimeOffset? PublishedUtc { get; set; }
        internal Dictionary<string, DateTimeOffset?> PackageModifiedUtc { get; }
        internal TimedRelease(GithubRelease release, DateTimeOffset? published, Dictionary<string, DateTimeOffset?> modified)
        {
            Name = release.Name; Prerelease = release.Prerelease; PublishedAt = release.PublishedAt; Assets = release.Assets;
            PublishedUtc = published; PackageModifiedUtc = modified;
        }
    }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var feed = await base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        // Full packages only: no unverified delta inputs or prereleases on stable installs.
        feed.Assets = feed.Assets.Where(asset => asset.Type == VelopackAssetType.Full
            && (Prerelease || asset.Version is null || !asset.Version.IsPrerelease)).ToArray();
        foreach (var asset in feed.Assets)
        {
            AppUpdates.ValidateTarget(asset, appId);
            var release = ((GitBaseAsset)asset).Release;
            string url = GetAssetUrlFromName(release, asset.FileName);
            string expected = $"{RepoUri}/releases/download/v{Uri.EscapeDataString(asset.Version.ToString())}/{Uri.EscapeDataString(asset.FileName)}";
            if (!string.Equals(url, expected, StringComparison.Ordinal))
                throw new InvalidDataException("The update package does not match its GitHub release tag.");
        }
        return feed;
    }
}

internal sealed class GitHubUpdateDownloader : HttpClientFileDownloader
{
    private readonly string _repository;
    private readonly Func<HttpMessageHandler> _transport;

    internal GitHubUpdateDownloader(string repository, Func<HttpMessageHandler>? transport = null)
    {
        _repository = AppUpdates.RepositoryUrl(repository) is not null ? repository : throw new ArgumentException("Invalid repository.", nameof(repository));
        _transport = transport ?? (() => new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
    }

    protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
    {
        var client = new HttpClient(new GitHubRedirectHandler(_repository, _transport()))
        {
            Timeout = TimeSpan.FromMinutes(Math.Clamp(timeout, 0.1, 15)),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpaceMaid-Updater");
        if (headers?.TryGetValue("Accept", out string? accept) == true)
            client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        return client;
    }

    // Do not retry cancelled/failed requests with a different URL or tag casing.
    protected override Task<T> TryDownloadThenLowercase<T>(Func<string, Task<T>> downloadFunc, string url) => downloadFunc(url);

    public override async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers,
        double timeout, CancellationToken cancelToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(timeout, 0.1, 15)));
        await base.DownloadFile(url, targetFile, progress, headers, timeout, deadline.Token).ConfigureAwait(false);
    }

    protected override async Task DownloadToStreamInternal(HttpClient client, string requestUri, Stream destination,
        Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancelToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const long maximum = 1_073_741_824;
        long? length = response.Content.Headers.ContentLength;
        if (length > maximum) throw new InvalidDataException("The update exceeds the download size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int lastProgress = 0, read;
        while ((read = await source.ReadAsync(buffer, cancelToken).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > maximum) throw new InvalidDataException("The update exceeds the download size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(false);
            int current = length is > 0 ? (int)Math.Min(100, total * 100 / length.Value) : 0;
            if (current >= lastProgress + 3) { progress?.Invoke(current); lastProgress = current; }
        }
        progress?.Invoke(100);
    }

    private sealed class GitHubRedirectHandler(string repository, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            ValidateUrl(uri, repository, redirect: false);
            for (int redirects = 0; ; redirects++)
            {
                using var next = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var header in request.Headers) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                var response = await base.SendAsync(next, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
                using (response)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location)
                        throw new HttpRequestException("The GitHub update download redirected too many times.");
                    uri = new Uri(uri, location);
                    ValidateUrl(uri, repository, redirect: true);
                }
            }
        }
    }

    private static void ValidateUrl(Uri uri, string repository, bool redirect)
    {
        string apiPath = $"/repos/{repository}/releases";
        bool trusted = uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && ((uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith(apiPath, StringComparison.Ordinal)
                    && (uri.AbsolutePath.Length == apiPath.Length || uri.AbsolutePath[apiPath.Length] == '/'))
                || (uri.Host == "github.com" && uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal))
                || (redirect && uri.Host == "release-assets.githubusercontent.com"));
        if (!trusted) throw new HttpRequestException("The update download left the trusted GitHub release location.");
    }
}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Bearing.Core.Updates;

namespace Bearing.Updates;

/// <summary>
/// <see cref="IReleaseNotes"/> over the GitHub Releases API for <see cref="UpdateFeed"/>.
/// <para>
/// This is the <b>one</b> place release notes come from. Velopack also carries notes — <c>vpk pack
/// --releaseNotes</c> writes them into <c>releases.&lt;channel&gt;.json</c>, and they arrive with every
/// update check — but that file lists only its own version's packages, and the updater only ever downloads
/// the newest release's copy of it. So the feed can describe the version you might install next and nothing
/// else, while this endpoint returns every release's notes in one request. Two sources for one thing, each
/// authoritative for a different subset, is a bug waiting to happen; the app reads this one.
/// </para>
/// <para>
/// It is the same endpoint Velopack's own <c>GithubSource</c> calls, so this adds a request rather than a
/// dependency — and the same 60/hr anonymous rate limit applies, which is what
/// <see cref="UpdateFeed.AccessToken"/> exists to lift.
/// </para>
/// </summary>
public sealed class GitHubReleaseNotes : IReleaseNotes
{
    /// <summary>How many releases back to read. Far more history than anyone scrolls, still one request.</summary>
    private const int PageSize = 30;

    /// <summary>
    /// Shared, as an <see cref="HttpClient"/> is meant to be. The timeout is short on purpose: this runs
    /// behind a menu click and behind the startup "what's new" check, and neither is worth a stalled minute.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _endpoint;
    private readonly string? _accessToken;

    public GitHubReleaseNotes(string? repoUrl = null, string? accessToken = null)
    {
        _endpoint = $"https://api.github.com/repos/{Slug(repoUrl ?? UpdateFeed.RepoUrl)}/releases?per_page={PageSize}";
        _accessToken = accessToken ?? UpdateFeed.AccessToken;
    }

    public async Task<IReadOnlyList<ReleaseNote>> FetchAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        // GitHub rejects an API request with no User-Agent outright, so this is required, not politeness.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Bearing", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (_accessToken is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        // The status line alone ("403") can't distinguish a spent rate limit from a bad token, and both are
        // things a user can act on — so say which, rather than making them guess.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Explain(response));

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        var notes = new List<ReleaseNote>();
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (Flag(release, "draft") || Flag(release, "prerelease")) continue;
            if (Text(release, "tag_name") is not { Length: > 0 } tag) continue;

            var version = StripTagPrefix(tag);
            notes.Add(new ReleaseNote(
                Version: version,
                Title: Text(release, "name") is { Length: > 0 } name ? name : version,
                Published: release.TryGetProperty("published_at", out var at)
                           && at.ValueKind is JsonValueKind.String
                           && at.TryGetDateTimeOffset(out var when) ? when : null,
                Markdown: Text(release, "body") ?? "",
                Url: Text(release, "html_url")));
        }

        // GitHub returns newest first already; it is not documented as a guarantee, and the dialog reads
        // top-down, so don't take it on trust.
        notes.Sort((a, b) => Compare(b, a));
        return notes;
    }

    /// <summary>
    /// <c>owner/repo</c> from the feed URL. Nothing clever: the feed URL is a constant in this assembly, so a
    /// malformed one is a build-time mistake, and failing loudly beats requesting a nonsense endpoint.
    /// </summary>
    internal static string Slug(string repoUrl)
    {
        var parts = repoUrl.TrimEnd('/').Split('/');
        if (parts.Length < 2 || parts[^1].Length == 0 || parts[^2].Length == 0)
            throw new ArgumentException($"Not a GitHub repository URL: {repoUrl}", nameof(repoUrl));
        var repo = parts[^1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return $"{parts[^2]}/{repo}";
    }

    /// <summary>Tags are <c>v0.3.0</c>; versions are <c>0.3.0</c>. Only the prefix differs, so only it is dropped.</summary>
    internal static string StripTagPrefix(string tag)
        => tag.Length > 1 && (tag[0] is 'v' or 'V') && char.IsDigit(tag[1]) ? tag[1..] : tag;

    /// <summary>
    /// Newest-first ordering. Semver where both sides parse — <c>0.10.0</c> is newer than <c>0.9.0</c>, which
    /// an ordinal compare gets backwards.
    /// <para>
    /// A tag that is not a version always sorts <i>below</i> one that is, rather than falling through to an
    /// ordinal compare against it. That is not a style choice: mixing the two rules per-pair is intransitive
    /// (<c>0.9.0 &lt; 0.10.0</c> by semver, <c>0.10.0 &lt; 0.1x &lt; 0.9.0</c> by text), and
    /// <see cref="List{T}.Sort"/> detects that and throws — which would take the whole dialog down over one
    /// hand-cut tag.
    /// </para>
    /// </summary>
    internal static int Compare(ReleaseNote a, ReleaseNote b)
    {
        var aIsVersion = Version.TryParse(Numeric(a.Version), out var va);
        var bIsVersion = Version.TryParse(Numeric(b.Version), out var vb);
        if (aIsVersion && bIsVersion) return va!.CompareTo(vb);
        if (aIsVersion != bIsVersion) return aIsVersion ? 1 : -1;
        return string.CompareOrdinal(a.Version, b.Version);
    }

    /// <summary>The <c>major.minor.patch</c> head of a version, dropping any <c>-beta.1</c> tail.</summary>
    private static string Numeric(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
    }

    private static string Explain(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        // "0 requests left" is the rate limit; a 403/401 with budget remaining is the credential.
        var spent = response.Headers.TryGetValues("X-RateLimit-Remaining", out var left)
                    && left.FirstOrDefault() == "0";
        return code switch
        {
            403 or 429 when spent => "GitHub's rate limit for this network is spent — try again later, "
                                     + "or set BEARING_UPDATE_TOKEN.",
            401 or 403 => "GitHub refused the request (check BEARING_UPDATE_TOKEN if one is set).",
            404 => "No releases found for this repository.",
            _ => $"GitHub returned {code} {response.ReasonPhrase}.",
        };
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
}

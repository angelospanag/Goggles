using System.Net.Http.Json;
using System.Text.Json;
using Goggles.Core.Api.Embed.Models;

namespace Goggles.Core.Api.Embed;

/// <summary>
/// The legacy account endpoints on <c>embed.gog.com</c> — where a user's owned library actually
/// lives. These predate the Galaxy API and answer with a different JSON dialect, so their types
/// stay in this namespace rather than being shared with <c>api.gog.com</c>.
/// </summary>
/// <remarks>
/// Every endpoint here needs a signed-in user, so the supplied <see cref="HttpClient"/> is expected
/// to be wired through <see cref="Auth.GogAuthHandler"/>.
/// </remarks>
public sealed class EmbedClient
{
    /// <summary>Games, as opposed to movies (2).</summary>
    private const int GameMediaType = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public EmbedClient(HttpClient http) => _http = http;

    /// <summary>The signed-in user, plus the checksums used to detect library changes.</summary>
    public Task<UserData> GetUserDataAsync(CancellationToken cancellationToken = default) =>
        GetRequiredAsync<UserData>(BuildUri("userData.json"), cancellationToken);

    /// <summary>Owned product ids with no metadata attached — cheap, and useful as a cross-check.</summary>
    public async Task<IReadOnlyList<long>> GetOwnedProductIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var owned = await GetRequiredAsync<OwnedProducts>(
            BuildUri("user/data/games"),
            cancellationToken).ConfigureAwait(false);

        return owned.Owned;
    }

    /// <summary>
    /// One page of the owned library. Pages are 1-based; the response reports how many there are.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="hidden">Whether to return the products the user has hidden instead of the rest.</param>
    /// <param name="search">Optional title filter.</param>
    public Task<FilteredProductsPage> GetFilteredProductsAsync(
        int page = 1,
        bool hidden = false,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var query = new Dictionary<string, string>
        {
            ["mediaType"] = GameMediaType.ToString(),
            ["page"] = page.ToString(),
            ["hiddenFlag"] = hidden ? "1" : "0",
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query["search"] = search;
        }

        return GetRequiredAsync<FilteredProductsPage>(
            BuildUri("account/getFilteredProducts", query),
            cancellationToken);
    }

    /// <summary>
    /// Detail for a single owned product, or <see langword="null"/> when the account does not own
    /// it — GOG answers with a literal <c>null</c> body rather than a 404.
    /// </summary>
    public Task<GameDetails?> GetGameDetailsAsync(
        long productId,
        CancellationToken cancellationToken = default) =>
        GetAsync<GameDetails>(BuildUri($"account/gameDetails/{productId}.json"), cancellationToken);

    /// <summary>
    /// Downloads one goodie from <see cref="GameDetails.Extras"/> into <paramref name="directory"/>
    /// and returns the path of the file written.
    /// </summary>
    /// <remarks>
    /// <c>manualUrl</c> is fetched from embed.gog.com with the bearer token and redirects to a signed
    /// CDN link. The redirect is followed by the transport, which drops the Authorization header on
    /// the way, so the token never reaches the CDN. The file name comes from GOG's answer rather than
    /// from the goodie's display name. The body streams to a <c>.part</c> file that is renamed only
    /// once complete, so an interrupted download never looks finished.
    /// </remarks>
    /// <param name="progress">Bytes written so far.</param>
    public async Task<string> DownloadExtraAsync(
        string manualUrl,
        string directory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Third-party input that becomes a request: keep it on embed.gog.com, under /downloads/.
        // A protocol-relative "//elsewhere" would otherwise move the bearer token to another host.
        if (!manualUrl.StartsWith("/downloads/", StringComparison.Ordinal) ||
            !Uri.TryCreate(GogHosts.Embed, manualUrl, out var uri) ||
            !string.Equals(uri.Host, GogHosts.Embed.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new GogApiException($"Refusing to download an extra from '{manualUrl}'.");
        }

        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new GogApiException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode);
        }

        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new GogApiException($"GOG answered {uri} with a web page instead of the file.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ExtraFileName(response, manualUrl));
        var partial = path + ".part";

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                progress?.Report(written);
            }
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// GOG's own name for the file: Content-Disposition if sent, else the last segment of the URL
    /// the redirect ended at, else the goodie's id. Reduced to a bare name either way, since it is
    /// about to be joined to a local directory.
    /// </summary>
    internal static string ExtraFileName(HttpResponseMessage response, string manualUrl)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var candidate = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');

        if (string.IsNullOrWhiteSpace(candidate) && response.RequestMessage?.RequestUri is { } final)
        {
            candidate = Uri.UnescapeDataString(final.Segments[^1]);
        }

        candidate = Path.GetFileName(candidate?.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..")
        {
            candidate = "extra-" + manualUrl.TrimEnd('/').Split('/')[^1];
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '_');
        }

        return candidate;
    }

    private async Task<T> GetRequiredAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        return await GetAsync<T>(uri, cancellationToken).ConfigureAwait(false)
               ?? throw new GogApiException($"GOG returned an empty body for {uri}.");
    }

    private async Task<T?> GetAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GogApiException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode,
                body);
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            // Signed-out sessions get an HTML login page with a 200 attached, so a parse failure
            // here is worth reporting as an API error rather than letting it surface raw.
            throw new GogApiException(
                $"GET {uri} returned a body that could not be read as {typeof(T).Name}: {exception.Message}");
        }
    }

    private static Uri BuildUri(string path, Dictionary<string, string>? query = null)
    {
        if (query is null || query.Count == 0)
        {
            return new Uri(GogHosts.Embed, path);
        }

        var encoded = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new Uri(GogHosts.Embed, $"{path}?{encoded}");
    }
}

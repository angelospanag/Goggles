using System.Net.Http.Json;
using System.Text.Json;
using Goggles.Core.Api.ContentSystem.Models;

namespace Goggles.Core.Api.ContentSystem;

/// <summary>
/// Build lists from <c>content-system.gog.com</c> — which builds exist for a product, on which OS
/// and branch, and where each one's metadata lives.
/// </summary>
public sealed class BuildsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public BuildsClient(HttpClient http) => _http = http;

    /// <summary>
    /// Lists builds for a product, newest first as GOG returns them.
    /// </summary>
    /// <param name="productId">GOG product id.</param>
    /// <param name="os">One of <c>windows</c>, <c>osx</c> or <c>linux</c>.</param>
    /// <param name="generation">
    /// 2 is what modern builds use; 1 exists for older titles and has a different manifest format.
    /// </param>
    public async Task<BuildList> GetBuildsAsync(
        long productId,
        string os = "windows",
        int generation = 2,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            GogHosts.ContentSystem,
            $"products/{productId}/os/{os}/builds?generation={generation}");

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GogApiException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode,
                body);
        }

        return await response.Content
                   .ReadFromJsonAsync<BuildList>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new GogApiException($"GOG returned an empty build list for {productId}.");
    }
}

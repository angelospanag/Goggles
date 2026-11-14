using System.Text.Json;
using Goggles.Core.Api.ContentSystem.Models;

namespace Goggles.Core.Api.ContentSystem;

/// <summary>
/// Signed, time-limited URLs for fetching depot chunks from GOG's CDN.
/// </summary>
/// <remarks>
/// Undocumented; modelled from live responses. Needs an authenticated <see cref="HttpClient"/>.
/// The tokens returned are short-lived, so fetch one per download session rather than caching it.
/// </remarks>
public sealed class SecureLinkClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public SecureLinkClient(HttpClient http) => _http = http;

    /// <summary>Signed CDN endpoints for a product's depot store.</summary>
    public async Task<SecureLinkResponse> GetSecureLinkAsync(
        long productId,
        string path = "/",
        int generation = 2,
        CancellationToken cancellationToken = default)
    {
        var json = await GetSecureLinkJsonAsync(productId, path, generation, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Deserialize<SecureLinkResponse>(json, JsonOptions)
               ?? throw new GogApiException($"Empty secure link response for {productId}.");
    }

    /// <param name="productId">GOG product id.</param>
    /// <param name="path">Path within the product's content store; <c>/</c> for the store root.</param>
    /// <param name="generation">2 for modern builds.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<string> GetSecureLinkJsonAsync(
        long productId,
        string path = "/",
        int generation = 2,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            GogHosts.ContentSystem,
            $"products/{productId}/secure_link" +
            $"?generation={generation}&path={Uri.EscapeDataString(path)}&_version=2");

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new GogApiException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode,
                body);
        }

        return body;
    }
}

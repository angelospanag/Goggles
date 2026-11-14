using System.Collections.Concurrent;
using Goggles.Core.Api.ContentSystem;
using Goggles.Core.Api.ContentSystem.Models;

namespace Goggles.Core.Downloads;

/// <summary>
/// Hands out CDN endpoints per product, fetching each product's signed link once per session.
/// </summary>
/// <remarks>
/// A build's depots can span several products (DLC), so links are cached per product id rather than
/// per download. The signing tokens are short-lived: if the CDN starts rejecting requests, call
/// <see cref="Invalidate"/> and ask again.
/// </remarks>
public sealed class SecureLinkProvider
{
    private readonly SecureLinkClient _client;
    private readonly ConcurrentDictionary<long, Lazy<Task<IReadOnlyList<SecureLinkEndpoint>>>> _cache = new();

    public SecureLinkProvider(SecureLinkClient client) => _client = client;

    public Task<IReadOnlyList<SecureLinkEndpoint>> GetEndpointsAsync(
        long productId,
        CancellationToken cancellationToken = default)
    {
        // Lazy keeps concurrent callers from each firing their own request for the same product.
        var entry = _cache.GetOrAdd(
            productId,
            id => new Lazy<Task<IReadOnlyList<SecureLinkEndpoint>>>(() => FetchAsync(id, cancellationToken)));

        return entry.Value;
    }

    public void Invalidate(long productId) => _cache.TryRemove(productId, out _);

    private async Task<IReadOnlyList<SecureLinkEndpoint>> FetchAsync(
        long productId,
        CancellationToken cancellationToken)
    {
        var link = await _client.GetSecureLinkAsync(productId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Preferred endpoints first, then the fallback-only ones, so retries can walk the list.
        var endpoints = link.Urls
            .Where(url => url.SupportsGeneration.Contains(2))
            .OrderBy(url => url.FallbackOnly)
            .ThenByDescending(url => url.Priority)
            .ToList();

        return endpoints.Count > 0
            ? endpoints
            : throw new InvalidOperationException(
                $"GOG offered no generation 2 CDN endpoint for product {productId}.");
    }
}

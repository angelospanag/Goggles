using System.Text.Json;

namespace Goggles.Core.Api.ContentSystem.Models;

/// <summary>
/// Response of <c>content-system.gog.com/products/{id}/secure_link</c>: a set of CDN endpoints, each
/// carrying a URL template and a short-lived signing token.
/// </summary>
public sealed record SecureLinkResponse
{
    public long ProductId { get; init; }

    /// <summary>Observed value: <c>depot</c>.</summary>
    public string? Type { get; init; }

    public IReadOnlyList<SecureLinkEndpoint> Urls { get; init; } = [];

    /// <summary>
    /// Picks the endpoint to download from: highest priority first, ignoring endpoints that do not
    /// support this generation or that are only there as a fallback.
    /// </summary>
    public SecureLinkEndpoint? SelectEndpoint(int generation = 2) => Urls
        .Where(url => !url.FallbackOnly && url.SupportsGeneration.Contains(generation))
        .MaxBy(url => url.Priority);
}

public sealed record SecureLinkEndpoint
{
    public string? EndpointName { get; init; }

    /// <summary>
    /// A template such as <c>{base_url}/token=nva={expires_at}~dirs={dirs}~token={token}{path}</c>,
    /// filled in from <see cref="Parameters"/>.
    /// </summary>
    public string? UrlFormat { get; init; }

    /// <summary>Mixed strings and numbers, so these stay as raw JSON values.</summary>
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } =
        new Dictionary<string, JsonElement>();

    public int Priority { get; init; }

    public int MaxFails { get; init; }

    public bool FallbackOnly { get; init; }

    public IReadOnlyList<int> SupportsGeneration { get; init; } = [];

    /// <summary>Builds the signed URL for one depot chunk, addressed by its compressed MD5.</summary>
    public Uri BuildChunkUri(string compressedMd5)
    {
        if (string.IsNullOrWhiteSpace(UrlFormat))
        {
            throw new InvalidOperationException($"Endpoint '{EndpointName}' has no URL template.");
        }

        var url = UrlFormat;

        foreach (var (key, value) in Parameters)
        {
            // The chunk hash extends the store path rather than the end of the URL: some templates
            // put query parameters after {path}, so appending to the finished string would break.
            var replacement = key == "path"
                ? $"{AsString(value)}/{GalaxyPath.Shard(compressedMd5)}"
                : AsString(value);

            url = url.Replace($"{{{key}}}", replacement, StringComparison.Ordinal);
        }

        return new Uri(url);
    }

    private static string AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
}

using System.IO.Compression;
using System.Text;

namespace Goggles.Core.Api.ContentSystem;

/// <summary>
/// Metadata blobs from <c>cdn.gog.com</c>.
/// </summary>
/// <remarks>
/// Returns raw JSON rather than typed models on purpose: the generation 2 manifest format is
/// undocumented, and types should be written against captured responses rather than guesses.
/// <para>
/// These blobs are public, so this client wants an <see cref="HttpClient"/> *without*
/// <c>GogAuthHandler</c> — there is no reason to hand the CDN a bearer token.
/// </para>
/// </remarks>
public sealed class CdnClient
{
    private readonly HttpClient _http;

    public CdnClient(HttpClient http) => _http = http;

    /// <summary>
    /// Where a metadata blob lives, given its hash. GOG shards these into two levels of two hex
    /// characters — undocumented, and derived from observed URLs.
    /// </summary>
    public static Uri BuildMetaUri(string hash) =>
        new(GogHosts.Cdn, $"content-system/v2/meta/{GalaxyPath.Shard(hash)}");

    /// <summary>Fetches and decompresses a metadata blob by hash.</summary>
    public Task<string> GetMetaJsonAsync(string hash, CancellationToken cancellationToken = default) =>
        GetMetaJsonAsync(BuildMetaUri(hash), cancellationToken);

    /// <summary>
    /// Fetches and decompresses a metadata blob from a URL — typically <see cref="Models.Build.Link"/>.
    /// </summary>
    public async Task<string> GetMetaJsonAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new GogApiException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode);
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return Decompress(payload);
    }

    /// <summary>
    /// Unwraps a metadata blob. GOG serves these zlib-compressed, but not always — and
    /// <see cref="HttpClient"/> may already have undone a transport-level gzip for us, so the
    /// wrapper is detected from the bytes rather than assumed.
    /// </summary>
    public static string Decompress(byte[] payload) =>
        payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(DecompressToBytes(payload));

    /// <summary>
    /// Unwraps a payload without assuming it is text — depot chunks are arbitrary file bytes.
    /// </summary>
    public static byte[] DecompressToBytes(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        using var source = new MemoryStream(payload);

        Stream reader = payload switch
        {
            [0x78, ..] => new ZLibStream(source, CompressionMode.Decompress),
            [0x1f, 0x8b, ..] => new GZipStream(source, CompressionMode.Decompress),
            _ => source,
        };

        using (reader)
        {
            using var buffer = new MemoryStream();
            reader.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}

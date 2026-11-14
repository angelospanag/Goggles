using System.IO.Compression;
using System.Net;
using System.Text;
using Goggles.Core.Api;
using Goggles.Core.Api.ContentSystem;

namespace Goggles.Core.Tests.Api.ContentSystem;

public class CdnClientTests
{
    private const string ManifestJson = """{"version":2,"baseProductId":"1136126792"}""";

    [Fact]
    public void Shards_a_hash_into_the_cdn_path()
    {
        // Observed on live build links: .../v2/meta/7a/15/7a15417223b2cc2328271e7bbcb65fc2
        Assert.Equal(
            "https://cdn.gog.com/content-system/v2/meta/7a/15/7a15417223b2cc2328271e7bbcb65fc2",
            CdnClient.BuildMetaUri("7a15417223b2cc2328271e7bbcb65fc2").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void Rejects_a_hash_it_cannot_shard(string hash)
    {
        Assert.ThrowsAny<ArgumentException>(() => CdnClient.BuildMetaUri(hash));
    }

    [Fact]
    public void Decompresses_zlib() =>
        Assert.Equal(ManifestJson, CdnClient.Decompress(ZLib(ManifestJson)));

    [Fact]
    public void Decompresses_gzip() =>
        Assert.Equal(ManifestJson, CdnClient.Decompress(GZip(ManifestJson)));

    [Fact]
    public void Passes_through_json_that_was_never_compressed() =>
        Assert.Equal(ManifestJson, CdnClient.Decompress(Encoding.UTF8.GetBytes(ManifestJson)));

    [Fact]
    public void Handles_an_empty_body() => Assert.Equal(string.Empty, CdnClient.Decompress([]));

    [Fact]
    public void Decompresses_arbitrary_bytes_without_treating_them_as_text()
    {
        // Depot chunks are file content, not JSON: bytes that are not valid UTF-8 must survive.
        var original = new byte[512];
        Random.Shared.NextBytes(original);

        using var buffer = new MemoryStream();
        using (var compressor = new ZLibStream(buffer, CompressionLevel.Optimal))
        {
            compressor.Write(original);
        }

        Assert.Equal(original, CdnClient.DecompressToBytes(buffer.ToArray()));
    }

    [Fact]
    public void Handles_an_empty_body_as_bytes() => Assert.Empty(CdnClient.DecompressToBytes([]));

    [Fact]
    public async Task Fetches_and_decompresses_a_blob_by_hash()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Bytes(ZLib(ManifestJson)));

        var json = await new CdnClient(new HttpClient(handler))
            .GetMetaJsonAsync(
                "7a15417223b2cc2328271e7bbcb65fc2",
                TestContext.Current.CancellationToken);

        Assert.Equal(ManifestJson, json);
        Assert.Equal(
            "https://cdn.gog.com/content-system/v2/meta/7a/15/7a15417223b2cc2328271e7bbcb65fc2",
            handler.RequestedUris.Single().ToString());
    }

    [Fact]
    public async Task Follows_a_build_link_verbatim()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Bytes(ZLib(ManifestJson)));
        var link = new Uri("https://gog-cdn-fastly.gog.com/content-system/v2/meta/7a/15/7a15417223b2cc2328271e7bbcb65fc2");

        var json = await new CdnClient(new HttpClient(handler)).GetMetaJsonAsync(
            link,
            TestContext.Current.CancellationToken);

        Assert.Equal(ManifestJson, json);
        Assert.Equal(link, handler.RequestedUris.Single());
    }

    [Fact]
    public async Task A_missing_blob_is_reported()
    {
        var handler = new StubHttpMessageHandler("gone", HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<GogApiException>(
            () => new CdnClient(new HttpClient(handler)).GetMetaJsonAsync(
                "7a15417223b2cc2328271e7bbcb65fc2",
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    private static byte[] ZLib(string text) => Compress(text, stream => new ZLibStream(stream, CompressionLevel.Optimal));

    private static byte[] GZip(string text) => Compress(text, stream => new GZipStream(stream, CompressionLevel.Optimal));

    private static byte[] Compress(string text, Func<Stream, Stream> wrap)
    {
        using var buffer = new MemoryStream();

        using (var compressor = wrap(buffer))
        {
            compressor.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }
}

using Goggles.Core.Api.ContentSystem;

namespace Goggles.Core.Tests.Api.ContentSystem;

public class SecureLinkTests
{
    private const string ChunkHash = "12c3cf8103ef7d265a5cb50688f86ee7";

    /// <summary>
    /// Captured from a live secure_link response, with the signing token replaced. Both endpoint
    /// shapes are kept: they place {path} differently, which is the whole reason substitution has
    /// to be template-aware.
    /// </summary>
    private const string SecureLinkJson = """
        {
          "product_id": 1136126792,
          "type": "depot",
          "urls": [
            {
              "endpoint_name": "fastly",
              "url_format": "{base_url}/token=nva={expires_at}~dirs={dirs}~token={token}{path}",
              "parameters": {
                "base_url": "https://gog-cdn-fastly.gog.com",
                "path": "/content-system/v2/store/1136126792",
                "token": "0000token0000",
                "expires_at": 1786579399,
                "dirs": 4
              },
              "priority": 10,
              "max_fails": 100,
              "supports_generation": [1, 2],
              "fallback_only": false
            },
            {
              "endpoint_name": "gcore",
              "url_format": "{base_url}/{path}?wsSecret={token}&wsTime={time}&prefix={prefix}",
              "parameters": {
                "base_url": "https://gog-cdn-lb.gog.com",
                "path": "content-system/v2/store/1136126792",
                "token": "1111token1111",
                "time": 1786579399,
                "prefix": "/content-system/v2/store/1136126792"
              },
              "priority": 1,
              "max_fails": 100,
              "supports_generation": [2],
              "fallback_only": false
            }
          ]
        }
        """;

    [Fact]
    public async Task Reads_the_endpoint_list()
    {
        var link = await FetchAsync(SecureLinkJson);

        Assert.Equal(1136126792, link.ProductId);
        Assert.Equal("depot", link.Type);
        Assert.Equal(["fastly", "gcore"], link.Urls.Select(url => url.EndpointName));
        Assert.Equal([1, 2], link.Urls[0].SupportsGeneration);
    }

    [Fact]
    public async Task Requests_the_documented_query_string()
    {
        var handler = new StubHttpMessageHandler(SecureLinkJson);

        await new SecureLinkClient(new HttpClient(handler)).GetSecureLinkAsync(
            1136126792,
            cancellationToken: TestContext.Current.CancellationToken);

        var uri = handler.RequestedUris.Single();
        Assert.Equal("/products/1136126792/secure_link", uri.AbsolutePath);
        Assert.Contains("generation=2", uri.Query);
        Assert.Contains("path=%2F", uri.Query);
        Assert.Contains("_version=2", uri.Query);
    }

    [Fact]
    public async Task Selects_the_highest_priority_endpoint()
    {
        var link = await FetchAsync(SecureLinkJson);

        Assert.Equal("fastly", link.SelectEndpoint()?.EndpointName);
    }

    [Fact]
    public async Task Skips_endpoints_that_do_not_support_the_generation()
    {
        var link = await FetchAsync(SecureLinkJson.Replace("[1, 2]", "[1]"));

        Assert.Equal("gcore", link.SelectEndpoint()?.EndpointName);
    }

    [Fact]
    public async Task Skips_fallback_only_endpoints()
    {
        var link = await FetchAsync(SecureLinkJson.Replace("\"fallback_only\": false", "\"fallback_only\": true"));

        Assert.Null(link.SelectEndpoint());
    }

    [Fact]
    public async Task Builds_a_chunk_url_for_a_path_suffix_template()
    {
        var link = await FetchAsync(SecureLinkJson);

        Assert.Equal(
            "https://gog-cdn-fastly.gog.com/token=nva=1786579399~dirs=4~token=0000token0000" +
            "/content-system/v2/store/1136126792/12/c3/12c3cf8103ef7d265a5cb50688f86ee7",
            link.Urls[0].BuildChunkUri(ChunkHash).ToString());
    }

    [Fact]
    public async Task Builds_a_chunk_url_when_query_parameters_follow_the_path()
    {
        var link = await FetchAsync(SecureLinkJson);
        var uri = link.Urls[1].BuildChunkUri(ChunkHash);

        // The hash extends the path, not the end of the URL.
        Assert.Equal(
            "/content-system/v2/store/1136126792/12/c3/12c3cf8103ef7d265a5cb50688f86ee7",
            uri.AbsolutePath);
        Assert.Contains("wsSecret=1111token1111", uri.Query);
    }

    [Fact]
    public void Shards_a_hash() =>
        Assert.Equal("12/c3/12c3cf8103ef7d265a5cb50688f86ee7", GalaxyPath.Shard(ChunkHash));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void Rejects_a_hash_that_is_too_short(string hash) =>
        Assert.ThrowsAny<ArgumentException>(() => GalaxyPath.Shard(hash));

    private static async Task<Core.Api.ContentSystem.Models.SecureLinkResponse> FetchAsync(string json) =>
        await new SecureLinkClient(new HttpClient(new StubHttpMessageHandler(json)))
            .GetSecureLinkAsync(1136126792);
}

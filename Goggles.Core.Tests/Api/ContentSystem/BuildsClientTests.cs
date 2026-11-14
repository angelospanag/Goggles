using System.Net;
using Goggles.Core.Api;
using Goggles.Core.Api.ContentSystem;

namespace Goggles.Core.Tests.Api.ContentSystem;

public class BuildsClientTests
{
    /// <summary>Trimmed from a real content-system response.</summary>
    private const string BuildsJson = """
        {
          "total_count": 18,
          "count": 2,
          "has_private_branches": true,
          "items": [
            {
              "build_id": "56879739721465886",
              "product_id": "1136126792",
              "os": "windows",
              "branch": "beta",
              "version_name": "2b8ffa30",
              "tags": ["receiver_v2", "csb_10_6_1_w_141"],
              "public": true,
              "date_published": "2023-09-19T18:36:09+0000",
              "generation": 2,
              "link": "https://gog-cdn-fastly.gog.com/content-system/v2/meta/4f/eb/4feb48374c1b9d0113391e995b6e8454"
            },
            {
              "build_id": "56631974714622235",
              "product_id": "1136126792",
              "os": "windows",
              "branch": null,
              "version_name": "5f6049d",
              "tags": ["receiver_v1"],
              "public": true,
              "date_published": "2023-06-26T09:12:00+0000",
              "generation": 2,
              "link": "https://gog-cdn-fastly.gog.com/content-system/v2/meta/7a/15/7a15417223b2cc2328271e7bbcb65fc2"
            }
          ]
        }
        """;

    [Fact]
    public async Task Requests_the_generation_2_build_list()
    {
        var handler = new StubHttpMessageHandler(BuildsJson);

        await new BuildsClient(new HttpClient(handler)).GetBuildsAsync(
            1136126792,
            "osx",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://content-system.gog.com/products/1136126792/os/osx/builds?generation=2",
            handler.RequestedUris.Single().ToString());
    }

    [Fact]
    public async Task Reads_the_snake_case_payload()
    {
        var handler = new StubHttpMessageHandler(BuildsJson);

        var builds = await new BuildsClient(new HttpClient(handler)).GetBuildsAsync(
            1136126792,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(18, builds.TotalCount);
        Assert.True(builds.HasPrivateBranches);

        var beta = builds.Items[0];
        Assert.Equal("56879739721465886", beta.BuildId);
        Assert.Equal("beta", beta.Branch);
        Assert.Equal(2, beta.Generation);
        Assert.Equal(["receiver_v2", "csb_10_6_1_w_141"], beta.Tags);
        Assert.Equal(
            "https://gog-cdn-fastly.gog.com/content-system/v2/meta/4f/eb/4feb48374c1b9d0113391e995b6e8454",
            beta.Link?.ToString());
    }

    [Fact]
    public async Task Reads_a_date_whose_offset_has_no_colon()
    {
        var handler = new StubHttpMessageHandler(BuildsJson);

        var builds = await new BuildsClient(new HttpClient(handler)).GetBuildsAsync(
            1136126792,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new DateTimeOffset(2023, 9, 19, 18, 36, 9, TimeSpan.Zero),
            builds.Items[0].DatePublished);
    }

    [Fact]
    public async Task The_default_branch_is_null_not_a_name()
    {
        var handler = new StubHttpMessageHandler(BuildsJson);

        var builds = await new BuildsClient(new HttpClient(handler)).GetBuildsAsync(
            1136126792,
            cancellationToken: TestContext.Current.CancellationToken);

        // Newest-first ordering spans branches, so "first item" is not the same as "current release".
        Assert.Equal("beta", builds.Items[0].Branch);
        Assert.Null(builds.Items.First(build => build.Branch is null).Branch);
        Assert.Equal("56631974714622235", builds.Items.First(build => build.Branch is null).BuildId);
    }

    [Fact]
    public async Task A_failed_request_carries_the_status()
    {
        var handler = new StubHttpMessageHandler("nope", HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<GogApiException>(
            () => new BuildsClient(new HttpClient(handler)).GetBuildsAsync(
                1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }
}

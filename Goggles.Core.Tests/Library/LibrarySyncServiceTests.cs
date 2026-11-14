using Goggles.Core.Api.Embed;
using Goggles.Core.Library;

namespace Goggles.Core.Tests.Library;

public class LibrarySyncServiceTests
{
    [Fact]
    public async Task Skips_the_fetch_when_the_checksum_is_unchanged()
    {
        var handler = Responder(checksum: "abc123");

        var result = await CreateService(handler).SyncAsync(
            knownChecksum: "abc123",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Changed);
        Assert.Equal("abc123", result.Checksum);
        Assert.Empty(result.Games);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Fetches_when_the_checksum_moved_on()
    {
        var handler = Responder(checksum: "def456");

        var result = await CreateService(handler).SyncAsync(
            knownChecksum: "abc123",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Changed);
        Assert.Equal("def456", result.Checksum);
        Assert.NotEmpty(result.Games);
    }

    [Fact]
    public async Task Carries_the_account_name_on_both_paths()
    {
        // The sync already fetches user data for the checksum, so the name comes along free.
        var fetched = await CreateService(Responder(checksum: "abc123")).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var skipped = await CreateService(Responder(checksum: "abc123")).SyncAsync(
            knownChecksum: "abc123",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("someone", fetched.AccountName);
        Assert.Equal("someone", skipped.AccountName);
    }

    [Fact]
    public async Task Fetches_when_no_checksum_is_known()
    {
        var handler = Responder(checksum: "abc123");

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Games.Count);
    }

    [Fact]
    public async Task Walks_every_page()
    {
        var handler = Responder(checksum: "abc123");

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], result.Games.Select(game => game.Id));
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(
            ["page=1", "page=2"],
            handler.RequestedUris
                .Where(uri => uri.AbsolutePath.EndsWith("getFilteredProducts", StringComparison.Ordinal))
                .Select(uri => uri.Query.Split('&').Single(part => part.Contains("page="))));
    }

    [Fact]
    public async Task Keeps_gogs_order_when_pages_arrive_together()
    {
        // Six pages: the first alone, then two batches. Page n holds the single game with id n.
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("userData.json", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json("""{"checksum":{"games":"abc123"}}""");
            }

            var page = uri.Query.Split('&').Single(part => part.Contains("page=", StringComparison.Ordinal))[^1];
            return StubHttpMessageHandler.Json($$"""{"totalPages":6,"products":[{"id":{{page}},"title":"Game {{page}}"}]}""");
        });

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], result.Games.Select(game => game.Id));
        Assert.Equal(6, result.PagesFetched);
    }

    [Fact]
    public async Task Stops_early_when_a_promised_page_comes_back_empty()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.EndsWith("userData.json", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json("""{"checksum":{"games":"abc123"}}""");
            }

            // Claims five pages but serves nothing after the first.
            return StubHttpMessageHandler.Json(uri.Query.Contains("page=1", StringComparison.Ordinal)
                ? """{"totalPages":5,"products":[{"id":1,"title":"One"}]}"""
                : """{"totalPages":5,"products":[]}""");
        });

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result.Games);
        Assert.Equal(1 + 1 + 4, handler.CallCount); // userData + page 1 + one batch of pages 2–5
    }

    [Fact]
    public async Task Includes_hidden_products_only_when_asked()
    {
        var handler = Responder(checksum: "abc123");
        var result = await CreateService(handler).SyncAsync(
            includeHidden: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Games, game => game.Id == 99 && game.IsHidden);
        Assert.Contains(handler.RequestedUris, uri => uri.Query.Contains("hiddenFlag=1"));

        var withoutHidden = Responder(checksum: "abc123");
        var visibleOnly = await CreateService(withoutHidden).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(visibleOnly.Games, game => game.Id == 99);
        Assert.DoesNotContain(withoutHidden.RequestedUris, uri => uri.Query.Contains("hiddenFlag=1"));
    }

    [Fact]
    public async Task Maps_a_product_onto_the_domain_model()
    {
        var handler = Responder(checksum: "abc123");

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var game = result.Games.First();

        Assert.Equal(1L, game.Id);
        Assert.Equal("Baldur's Gate", game.Title);
        Assert.Equal("baldurs_gate", game.Slug);
        Assert.Equal("Role-playing", game.Category);
        Assert.Equal(GamePlatforms.Windows | GamePlatforms.Mac, game.Platforms);
        Assert.Equal(4, game.CommunityRating);
        Assert.Equal(2, game.DlcCount);
        Assert.True(game.IsGalaxyCompatible);
        Assert.Equal(["12"], game.TagIds);
        Assert.Equal(
            new DateTimeOffset(1998, 12, 21, 0, 0, 0, TimeSpan.FromHours(2)),
            game.ReleasedAt);
    }

    [Fact]
    public async Task Skips_the_hidden_pass_when_the_account_has_nothing_hidden()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("userData.json", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json("""{"checksum":{"games":"abc123"}}""")
                : StubHttpMessageHandler.Json(
                    """{"totalPages":1,"hasHiddenProducts":false,"products":[{"id":1,"title":"One"}]}"""));

        await CreateService(handler).SyncAsync(
            includeHidden: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.RequestedUris, uri => uri.Query.Contains("hiddenFlag=1"));
    }

    [Fact]
    public async Task Turns_protocol_relative_images_into_absolute_https_urls()
    {
        var handler = Responder(checksum: "abc123");

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://images.gog.com/abc123"), result.Games[0].ImageBaseUrl);
        Assert.Null(result.Games[1].ImageBaseUrl);
    }

    [Fact]
    public async Task Reports_no_platforms_when_gog_omits_worksOn()
    {
        var handler = Responder(checksum: "abc123");

        var result = await CreateService(handler).SyncAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GamePlatforms.None, result.Games[1].Platforms);
    }

    [Fact]
    public async Task Cancellation_stops_the_walk()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }

            return request.RequestUri.AbsolutePath.EndsWith("userData.json", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json("""{"checksum":{"games":"abc123"}}""")
                : StubHttpMessageHandler.Json("""{"totalPages":9,"products":[{"id":1,"title":"One"}]}""");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(handler).SyncAsync(cancellationToken: cancellation.Token));

        // userData + page 1 + the batch already in flight (pages 2–5); pages 6–9 are never asked for.
        Assert.Equal(1 + 1 + 4, handler.CallCount);
    }

    private static LibrarySyncService CreateService(StubHttpMessageHandler handler) =>
        new(new EmbedClient(new HttpClient(handler)));

    /// <summary>Serves a two-page visible library plus a one-page hidden library.</summary>
    private static StubHttpMessageHandler Responder(string checksum) =>
        new((request, _) =>
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.EndsWith("userData.json", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(
                    $$$"""{"username":"someone","checksum":{"games":"{{{checksum}}}"}}""");
            }

            if (uri.Query.Contains("hiddenFlag=1", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json("""
                    {
                      "page": 1, "totalPages": 1,
                      "products": [{ "id": 99, "title": "Hidden Thing", "isHidden": true }]
                    }
                    """);
            }

            return StubHttpMessageHandler.Json(
                uri.Query.Contains("page=1", StringComparison.Ordinal) ? FirstPage : SecondPage);
        });

    private const string FirstPage = """
        {
          "page": 1,
          "totalPages": 2,
          "totalProducts": 3,
          "hasHiddenProducts": true,
          "products": [
            {
              "id": 1,
              "title": "Baldur's Gate",
              "slug": "baldurs_gate",
              "image": "//images.gog.com/abc123",
              "category": "Role-playing",
              "rating": 4,
              "dlcCount": 2,
              "isGalaxyCompatible": true,
              "worksOn": { "Windows": true, "Mac": true, "Linux": false },
              "releaseDate": {
                "date": "1998-12-21 00:00:00.000000",
                "timezone_type": 1,
                "timezone": "+02:00"
              },
              "tags": ["12"]
            },
            {
              "id": 2,
              "title": "A Game With Nothing Set"
            }
          ]
        }
        """;

    private const string SecondPage = """
        {
          "page": 2,
          "totalPages": 2,
          "totalProducts": 3,
          "products": [
            {
              "id": 3,
              "title": "Papers, Please",
              "image": "//images.gog.com/xyz789",
              "worksOn": { "Windows": true, "Mac": false, "Linux": true }
            }
          ]
        }
        """;
}

using System.Net;
using Goggles.Core.Api;
using Goggles.Core.Api.Embed;

namespace Goggles.Core.Tests.Api.Embed;

public class EmbedClientTests
{
    [Fact]
    public async Task GetUserDataAsync_reads_the_identity_and_checksums()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "country": "GB",
              "isLoggedIn": true,
              "userId": "48485266454929432",
              "galaxyUserId": "48485266454929432",
              "username": "someone",
              "email": "someone@example.com",
              "avatar": "https://images.gog.com/avatar",
              "checksum": {
                "cart": "0",
                "games": "abc123",
                "wishlist": "def456",
                "games_rating": "ghi789",
                "reviews_votes": "jkl012"
              },
              "purchasedItems": { "games": 42, "movies": 1 }
            }
            """);

        var user = await new EmbedClient(new HttpClient(handler)).GetUserDataAsync(
            TestContext.Current.CancellationToken);

        Assert.True(user.IsLoggedIn);
        Assert.Equal("someone", user.Username);
        Assert.Equal("abc123", user.Checksum?.Games);

        // These two keys are snake_case where the rest of the payload is camelCase.
        Assert.Equal("ghi789", user.Checksum?.GamesRating);
        Assert.Equal("jkl012", user.Checksum?.ReviewsVotes);

        Assert.Equal(42, user.PurchasedItems?.Games);
        Assert.Equal("https://embed.gog.com/userData.json", handler.RequestedUris.Single().ToString());
    }

    [Fact]
    public async Task GetOwnedProductIdsAsync_reads_the_id_array()
    {
        var handler = new StubHttpMessageHandler("""{"owned":[1207658930,1207666633,3000]}""");

        var ids = await new EmbedClient(new HttpClient(handler)).GetOwnedProductIdsAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal([1207658930L, 1207666633L, 3000L], ids);
        Assert.Equal("https://embed.gog.com/user/data/games", handler.RequestedUris.Single().ToString());
    }

    [Fact]
    public async Task GetFilteredProductsAsync_sends_the_paging_parameters()
    {
        var handler = new StubHttpMessageHandler(OnePageOfProducts);

        await new EmbedClient(new HttpClient(handler)).GetFilteredProductsAsync(
            page: 3,
            hidden: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var query = handler.RequestedUris.Single().Query;
        Assert.Contains("mediaType=1", query);
        Assert.Contains("page=3", query);
        Assert.Contains("hiddenFlag=1", query);
    }

    [Fact]
    public async Task GetFilteredProductsAsync_maps_a_page()
    {
        var handler = new StubHttpMessageHandler(OnePageOfProducts);

        var page = await new EmbedClient(new HttpClient(handler)).GetFilteredProductsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, page.TotalPages);
        Assert.Equal(51, page.TotalProducts);

        var game = page.Products.Single();
        Assert.Equal(1207658930L, game.Id);
        Assert.Equal("Baldur's Gate", game.Title);
        Assert.True(game.WorksOn?.Windows);
        Assert.False(game.WorksOn?.Linux);
        Assert.True(game.Availability?.IsAvailableInAccount);
        Assert.Equal("FAVORITE", page.Tags.Single().Name);
    }

    [Fact]
    public async Task GetFilteredProductsAsync_rejects_a_page_below_one()
    {
        var client = new EmbedClient(new HttpClient(new StubHttpMessageHandler(OnePageOfProducts)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.GetFilteredProductsAsync(
                page: 0,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""["12","34"]""")]
    [InlineData("""[{"id":"12","name":"FAVORITE"},{"id":"34","name":"BACKLOG"}]""")]
    [InlineData("[12,34]")]
    public async Task Product_tags_are_read_in_every_shape_gog_sends(string tagsJson)
    {
        var handler = new StubHttpMessageHandler($$"""
            {
              "totalPages": 1,
              "products": [{ "id": 1, "title": "A Game", "tags": {{tagsJson}} }]
            }
            """);

        var page = await new EmbedClient(new HttpClient(handler)).GetFilteredProductsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["12", "34"], page.Products.Single().Tags);
    }

    [Fact]
    public async Task Library_tag_counts_are_read_as_numbers()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "totalPages": 1,
              "tags": [{ "id": "12", "name": "FAVORITE", "productCount": 3 }],
              "products": []
            }
            """);

        var page = await new EmbedClient(new HttpClient(handler)).GetFilteredProductsAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, page.Tags.Single().ProductCount);
    }

    [Fact]
    public async Task GetGameDetailsAsync_reads_detail_and_dlcs()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "title": "The Witcher 3: Wild Hunt",
              "backgroundImage": "https://images.gog.com/bg.jpg",
              "cdKey": "",
              "isPreOrder": false,
              "releaseTimestamp": 1431993600,
              "dlcs": [{ "title": "Hearts of Stone" }, { "title": "Blood and Wine" }]
            }
            """);

        var details = await new EmbedClient(new HttpClient(handler)).GetGameDetailsAsync(
            1207658930,
            TestContext.Current.CancellationToken);

        Assert.Equal("The Witcher 3: Wild Hunt", details?.Title);
        Assert.Equal(["Hearts of Stone", "Blood and Wine"], details?.Dlcs.Select(dlc => dlc.Title));
        Assert.Equal(
            "https://embed.gog.com/account/gameDetails/1207658930.json",
            handler.RequestedUris.Single().ToString());
    }

    [Fact]
    public async Task GetGameDetailsAsync_returns_null_for_an_unowned_product()
    {
        var handler = new StubHttpMessageHandler("null");

        var details = await new EmbedClient(new HttpClient(handler)).GetGameDetailsAsync(
            1,
            TestContext.Current.CancellationToken);

        Assert.Null(details);
    }

    [Fact]
    public async Task A_failed_request_carries_the_status_and_body()
    {
        var handler = new StubHttpMessageHandler("""{"error":"nope"}""", HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<GogApiException>(
            () => new EmbedClient(new HttpClient(handler)).GetUserDataAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("nope", exception.ResponseBody);
    }

    [Fact]
    public async Task A_login_page_served_with_a_200_is_reported_as_an_api_error()
    {
        // Signed-out embed sessions answer with HTML and no error status at all.
        var handler = new StubHttpMessageHandler("<!DOCTYPE html><html><body>Sign in</body></html>");

        var exception = await Assert.ThrowsAsync<GogApiException>(
            () => new EmbedClient(new HttpClient(handler)).GetUserDataAsync(
                TestContext.Current.CancellationToken));

        Assert.Contains("could not be read as UserData", exception.Message);
    }

    private const string OnePageOfProducts = """
        {
          "page": 1,
          "totalPages": 2,
          "totalProducts": 51,
          "productsPerPage": 50,
          "moviesCount": 0,
          "sortBy": "date_purchased",
          "tags": [{ "id": "12", "name": "FAVORITE", "productCount": 3 }],
          "products": [
            {
              "id": 1207658930,
              "title": "Baldur's Gate",
              "slug": "baldurs_gate",
              "url": "/game/baldurs_gate",
              "image": "//images.gog.com/abc123",
              "category": "Role-playing",
              "dlcCount": 0,
              "isGame": true,
              "isMovie": false,
              "isHidden": false,
              "isGalaxyCompatible": true,
              "worksOn": { "Windows": true, "Mac": true, "Linux": false },
              "availability": { "isAvailable": true, "isAvailableInAccount": true },
              "tags": []
            }
          ]
        }
        """;
}

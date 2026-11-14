using System.Net;
using Goggles.Core.Api.Gameplay;

namespace Goggles.Core.Tests.Api.Gameplay;

public class GameplayClientTests
{
    [Fact]
    public async Task GetAchievementsAsync_computes_unlocked_from_date_unlocked()
    {
        // gameplay.gog.com answers in snake_case (confirmed against a live account), unlike
        // embed.gog.com's camelCase — these fixtures use the real wire shape. The no-colon
        // offset ("+0000", not "+00:00") is GOG's real date_unlocked format too.
        var handler = new StubHttpMessageHandler("""
            {
              "total_count": 3,
              "items": [
                { "achievement_key": "a", "name": "A", "date_unlocked": "2026-08-20T10:00:00+0000" },
                { "achievement_key": "b", "name": "B", "date_unlocked": "2026-08-22T10:00:00+0000" },
                { "achievement_key": "c", "name": "C", "date_unlocked": null }
              ]
            }
            """);

        var result = await new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
            123,
            456,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(
            "https://gameplay.gog.com/clients/123/users/456/achievements",
            handler.RequestedUris.Single().ToString());

        var unlocked = result.Unlocked();
        Assert.Equal(2, unlocked.Count);
        // Most recently unlocked first.
        Assert.Equal(["b", "a"], unlocked.Select(item => item.Key));
    }

    [Fact]
    public async Task GetAchievementsAsync_keeps_the_visible_flag()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "total_count": 2,
              "items": [
                { "achievement_key": "secret", "name": "???", "visible": false },
                { "achievement_key": "public", "name": "Public", "visible": true }
              ]
            }
            """);

        var result = await new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
            1,
            2,
            TestContext.Current.CancellationToken);
        var byKey = result.Items.ToDictionary(item => item.Key);

        Assert.False(byKey["secret"].Visible);
        Assert.True(byKey["public"].Visible);
    }

    [Fact]
    public async Task GetAchievementsAsync_treats_an_unparseable_date_as_not_unlocked()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "total_count": 1,
              "items": [{ "achievement_key": "a", "date_unlocked": "not-a-real-date" }]
            }
            """);

        var result = await new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Unlocked());
    }

    [Fact]
    public async Task GetAchievementsAsync_walks_pages_via_page_token()
    {
        var handler = new StubHttpMessageHandler((request, ordinal) => ordinal switch
        {
            1 => StubHttpMessageHandler.Json(
                """{"total_count":2,"page_token":"next","items":[{"achievement_key":"a"}]}"""),
            _ => StubHttpMessageHandler.Json(
                """{"total_count":2,"items":[{"achievement_key":"b"}]}"""),
        });

        var result = await new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("page_token=next", handler.RequestedUris[1].Query);
    }

    [Fact]
    public async Task GetAchievementsAsync_stops_on_a_non_empty_sentinel_token_once_total_is_reached()
    {
        // Confirmed against a live account: GOG can return page_token: "0" — non-empty — even
        // though every item already came back in that one page.
        var handler = new StubHttpMessageHandler(
            """{"total_count":2,"page_token":"0","items":[{"achievement_key":"a"},{"achievement_key":"b"}]}""");

        var result = await new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAchievementsAsync_stops_at_max_pages_on_a_repeating_token()
    {
        var handler = new StubHttpMessageHandler(
            """{"total_count":1,"page_token":"again","items":[]}""");

        await new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(GameplayClient.MaxAchievementPages, handler.CallCount);
    }

    [Fact]
    public async Task GetAchievementsAsync_non_ok_status_throws()
    {
        var handler = new StubHttpMessageHandler(
            """{"error":"invalid_token"}""", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<Goggles.Core.Api.GogApiException>(
            () => new GameplayClient(new HttpClient(handler)).GetAchievementsAsync(
                1,
                2,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSessionSummaryAsync_reads_the_time_sum_shape()
    {
        var handler = new StubHttpMessageHandler("""{"time_sum": 300}""");

        var result = await new GameplayClient(new HttpClient(handler)).GetSessionSummaryAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.True(result.Available);
        Assert.Equal(300, result.Minutes);
        Assert.Equal(
            "https://gameplay.gog.com/games/1/users/2/sessions",
            handler.RequestedUris.Single().ToString());
    }

    [Fact]
    public async Task GetSessionSummaryAsync_reads_the_sessions_list_shape()
    {
        var handler = new StubHttpMessageHandler("""{"sessions": [{"time": 30}, {"time": 45}]}""");

        var result = await new GameplayClient(new HttpClient(handler)).GetSessionSummaryAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.True(result.Available);
        Assert.Equal(75, result.Minutes);
    }

    [Fact]
    public async Task GetSessionSummaryAsync_degrades_gracefully_on_an_unrecognized_shape()
    {
        var handler = new StubHttpMessageHandler("""{"something_else_entirely": 42}""");

        var result = await new GameplayClient(new HttpClient(handler)).GetSessionSummaryAsync(
            1,
            2,
            TestContext.Current.CancellationToken);

        Assert.False(result.Available);
    }
}

using System.Net;
using Goggles.Core.Api.Gameplay;
using Goggles.Core.Library;

namespace Goggles.Core.Tests.Library;

public class GameStatsCacheTests
{
    [Fact]
    public async Task GetStatsAsync_fetches_then_caches()
    {
        var achievementRequests = 0;
        var sessionRequests = 0;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("achievements", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref achievementRequests);
                return StubHttpMessageHandler.Json(
                    """{"total_count":2,"items":[{"achievement_key":"a","date_unlocked":"2026-08-20T10:00:00Z"}]}""");
            }

            Interlocked.Increment(ref sessionRequests);
            return StubHttpMessageHandler.Json("""{"time_sum": 120}""");
        });

        using var cache = CreateCache(handler);

        var first = await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, first.AchievementsTotal);
        Assert.Equal(1, first.AchievementsUnlocked);
        Assert.True(first.HasPlaytime);
        Assert.Equal(120, first.PlaytimeMinutes);

        await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);

        Assert.Equal(1, achievementRequests);
        Assert.Equal(1, sessionRequests);
    }

    [Fact]
    public async Task GetStatsAsync_refetches_once_the_entry_is_older_than_the_ttl()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("achievements", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json("""{"total_count":0,"items":[]}""")
                : StubHttpMessageHandler.Json("""{"time_sum": 120}"""));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

        using var cache = CreateCache(handler, time);

        await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(10));
        await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.CallCount);

        time.Advance(TimeSpan.FromSeconds(1));
        await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task GetStatsAsync_does_not_share_a_cached_entry_between_users()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("achievements", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json("""{"total_count":0,"items":[]}""")
                : StubHttpMessageHandler.Json(request.RequestUri.AbsolutePath.Contains("/users/2/")
                    ? """{"time_sum": 120}"""
                    : """{"time_sum": 5}"""));

        using var cache = CreateCache(handler);

        var first = await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);
        var second = await cache.GetStatsAsync(1, 3, TestContext.Current.CancellationToken);

        Assert.Equal(120, first.PlaytimeMinutes);
        Assert.Equal(5, second.PlaytimeMinutes);
    }

    [Fact]
    public async Task GetStatsAsync_does_not_cache_a_fetch_where_everything_failed()
    {
        var failing = true;
        var handler = new StubHttpMessageHandler((request, _) =>
            Volatile.Read(ref failing)
                ? StubHttpMessageHandler.Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)
                : request.RequestUri!.AbsolutePath.EndsWith("achievements", StringComparison.Ordinal)
                    ? StubHttpMessageHandler.Json("""{"total_count":0,"items":[]}""")
                    : StubHttpMessageHandler.Json("""{"time_sum": 30}"""));

        using var cache = CreateCache(handler);

        var failed = await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);
        Volatile.Write(ref failing, false);
        var retried = await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);

        Assert.False(failed.HasPlaytime);
        Assert.Equal(30, retried.PlaytimeMinutes);
    }

    [Fact]
    public async Task GetStatsAsync_achievements_failure_does_not_discard_playtime()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("achievements", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)
                : StubHttpMessageHandler.Json("""{"time_sum": 60}"""));

        using var cache = CreateCache(handler);
        var stats = await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);

        Assert.False(stats.HasAchievements);
        Assert.True(stats.HasPlaytime);
        Assert.Equal(60, stats.PlaytimeMinutes);
    }

    [Fact]
    public async Task GetStatsAsync_playtime_failure_does_not_discard_achievements()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("sessions", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)
                : StubHttpMessageHandler.Json(
                    """{"total_count":1,"items":[{"achievement_key":"a","date_unlocked":"2026-08-20T10:00:00Z"}]}"""));

        using var cache = CreateCache(handler);
        var stats = await cache.GetStatsAsync(1, 2, TestContext.Current.CancellationToken);

        Assert.True(stats.HasAchievements);
        Assert.Equal(1, stats.AchievementsUnlocked);
        Assert.False(stats.HasPlaytime);
    }

    [Fact]
    public async Task GetStatsAsync_throttles_concurrent_fetches()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new object();
        using var release = new SemaphoreSlim(0);

        var handler = new StubHttpMessageHandler((_, _) =>
        {
            lock (gate)
            {
                inFlight++;
                maxInFlight = Math.Max(maxInFlight, inFlight);
            }

            release.Wait();

            lock (gate)
            {
                inFlight--;
            }

            return StubHttpMessageHandler.Json("""{"total_count":0,"items":[]}""");
        });

        using var cache = CreateCache(handler);

        // The stub blocks synchronously (no real network I/O to yield on), so each call must run
        // on its own pool thread — otherwise the first call would block the test thread itself
        // before the rest could even start.
        var tasks = Enumerable.Range(0, 12)
            .Select(id => Task.Run(() => cache.GetStatsAsync(id, 1)))
            .ToArray();

        // Let every request pile up against the semaphore.
        await Task.Delay(150, TestContext.Current.CancellationToken);
        release.Release(100);
        await Task.WhenAll(tasks);

        // The stub is fully synchronous, so within one GetStatsAsync call the achievements request
        // blocks before the session request can even start — the ceiling this actually exercises is
        // the outer per-product semaphore, not per-call sub-request concurrency.
        Assert.True(maxInFlight <= 6, $"max concurrent fetches observed = {maxInFlight}, want <= 6");
    }

    private static GameStatsCache CreateCache(HttpMessageHandler handler, TimeProvider? time = null) =>
        new(new GameplayClient(new HttpClient(handler)), time);
}

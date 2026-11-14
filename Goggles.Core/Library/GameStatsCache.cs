using System.Collections.Concurrent;
using Goggles.Core.Api.Gameplay;

namespace Goggles.Core.Library;

/// <summary>
/// A concurrency-throttled, TTL-cached fetcher for per-product achievement and play-time stats,
/// mirroring <c>Goggles.Desktop/Services/CoverImageCache.cs</c>'s shape. Unlike cover art, these values
/// change over the course of a session and aren't worth persisting to disk, so caching here is
/// in-memory only.
/// </summary>
public sealed class GameStatsCache : IDisposable
{
    private const int MaxConcurrentFetches = 6;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly GameplayClient _client;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _throttle = new(MaxConcurrentFetches);
    // Keyed by user as well as product: the session outlives sign-out, so a product-only key would
    // show the previous account's achievements and play time to the next one until the TTL ran out.
    private readonly ConcurrentDictionary<(long ProductId, long UserId), (GameStats Stats, DateTimeOffset FetchedAt)> _entries = new();

    public GameStatsCache(GameplayClient client, TimeProvider? timeProvider = null)
    {
        _client = client;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void Dispose() => _throttle.Dispose();

    /// <summary>
    /// Returns <paramref name="productId"/>'s <see cref="GameStats"/> for <paramref name="userId"/>,
    /// from cache if still fresh, otherwise fetching and caching it. Never throws — see
    /// <see cref="GameStats"/>'s field comments for how a fetch failure degrades instead.
    /// </summary>
    public async Task<GameStats> GetStatsAsync(
        long productId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(productId, userId, out var cached))
        {
            return cached;
        }

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have populated the cache while this one waited for a slot.
            if (TryGetFresh(productId, userId, out var stillCached))
            {
                return stillCached;
            }

            var (stats, anyFetched) = await FetchAsync(productId, userId, cancellationToken).ConfigureAwait(false);

            // When both halves failed there is nothing worth keeping — most likely the session died
            // or the network dropped mid-load — so the next ask tries again instead of serving an
            // empty entry for the rest of the TTL.
            if (anyFetched)
            {
                _entries[(productId, userId)] = (stats, _time.GetUtcNow());
            }

            return stats;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<(GameStats Stats, bool AnyFetched)> FetchAsync(
        long productId,
        long userId,
        CancellationToken cancellationToken)
    {
        var achievementsTask = SafeAsync(() => _client.GetAchievementsAsync(productId, userId, cancellationToken));
        var sessionTask = SafeAsync(() => _client.GetSessionSummaryAsync(productId, userId, cancellationToken));

        await Task.WhenAll(achievementsTask, sessionTask).ConfigureAwait(false);
        var achievements = await achievementsTask.ConfigureAwait(false);
        var session = await sessionTask.ConfigureAwait(false);

        var stats = new GameStats();

        if (achievements is not null)
        {
            var unlocked = achievements.Unlocked();
            stats = stats with
            {
                AchievementsUnlocked = unlocked.Count,
                AchievementsTotal = achievements.Total,
                HasAchievements = achievements.Total > 0,
                UnlockedAchievements = unlocked,
                AllAchievements = achievements.AllSorted(),
            };
        }

        if (session is { Available: true })
        {
            stats = stats with { PlaytimeMinutes = session.Minutes, HasPlaytime = true };
        }

        return (stats, achievements is not null || session is not null);
    }

    /// <summary>Runs <paramref name="fetch"/>, turning any exception into a null result — achievements
    /// and playtime are fetched independently, so one failing must never take the other down with it.</summary>
    private static async Task<T?> SafeAsync<T>(Func<Task<T>> fetch) where T : class
    {
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool TryGetFresh(long productId, long userId, out GameStats stats)
    {
        if (_entries.TryGetValue((productId, userId), out var entry) && _time.GetUtcNow() - entry.FetchedAt <= Ttl)
        {
            stats = entry.Stats;
            return true;
        }

        stats = new GameStats();
        return false;
    }
}

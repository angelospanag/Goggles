namespace Goggles.Core.Api.Gameplay.Models;

/// <summary>The result of <see cref="GameplayClient.GetAchievementsAsync"/>: every achievement
/// definition for a product, with this user's unlock state.</summary>
public sealed record Achievements
{
    /// <summary>The achievement count GOG reports, independent of how many <see cref="Items"/> were
    /// actually walked (see <see cref="GameplayClient.MaxAchievementPages"/>).</summary>
    public int Total { get; init; }

    public IReadOnlyList<Achievement> Items { get; init; } = [];

    /// <summary>The subset of <see cref="Items"/> that have actually been unlocked, sorted by
    /// <see cref="Achievement.UnlockedAt"/> descending (most recent first).</summary>
    public IReadOnlyList<Achievement> Unlocked() =>
        Items
            .Where(item => item.UnlockedAt is not null)
            .OrderByDescending(item => item.UnlockedAt)
            .ToList();

    /// <summary>Every <see cref="Items"/> entry — locked and unlocked — for a "my progress" view:
    /// unlocked first (most recent unlock first), then locked, alphabetically by name.</summary>
    public IReadOnlyList<Achievement> AllSorted() =>
        Items
            .OrderByDescending(item => item.UnlockedAt is not null)
            .ThenByDescending(item => item.UnlockedAt)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
}

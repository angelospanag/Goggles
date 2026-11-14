using Goggles.Core.Api.Gameplay.Models;

namespace Goggles.Core.Library;

/// <summary>
/// A product's achievement and play-time summary for one user. Achievements and playtime degrade
/// independently: a failure fetching one never discards a successful fetch of the other, since
/// this is specifically the resilience layer over <c>gameplay.gog.com</c> — a host with no
/// official documentation.
/// </summary>
public sealed record GameStats
{
    public int AchievementsUnlocked { get; init; }

    public int AchievementsTotal { get; init; }

    public bool HasAchievements { get; init; }

    /// <summary>Sorted most-recently-unlocked first — ready to render as an activity feed.</summary>
    public IReadOnlyList<Achievement> UnlockedAchievements { get; init; } = [];

    /// <summary>Every achievement — locked and unlocked — for a "my progress" view. Sorted via
    /// <see cref="Achievements.AllSorted"/>: unlocked first (most recent first), then locked
    /// alphabetically.</summary>
    public IReadOnlyList<Achievement> AllAchievements { get; init; } = [];

    public int PlaytimeMinutes { get; init; }

    public bool HasPlaytime { get; init; }
}

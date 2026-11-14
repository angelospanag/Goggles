using Avalonia.Media.Imaging;

namespace Goggles.Desktop.ViewModels;

/// <summary>One achievement in the detail page's activity feed, with its icon already resolved.</summary>
public sealed record AchievementActivityItem(string Name, Bitmap? Icon);

/// <summary>Every achievement unlocked on one calendar day, newest group first — mirrors GOG
/// Galaxy's "earned N achievements · 2 days ago" activity entries.</summary>
public sealed record AchievementActivityGroup(string Summary, string RelativeTime, IReadOnlyList<AchievementActivityItem> Items);

/// <summary>One entry in the "My progress" tab's full achievement list — locked or unlocked.
/// IsSecret means Name/Description/Icon are deliberately left unset: GOG hasn't revealed what this
/// achievement is yet, and showing them would spoil it.</summary>
public sealed record AchievementProgressItem(
    string Name,
    string? Description,
    Bitmap? Icon,
    bool IsUnlocked,
    bool IsSecret,
    string? RelativeUnlockedAt);

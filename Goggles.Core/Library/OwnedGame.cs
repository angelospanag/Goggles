namespace Goggles.Core.Library;

/// <summary>
/// A game in the user's library, as Goggles thinks of it — free of the embed API's wire shape so
/// the rest of the app never has to care which endpoint it came from.
/// </summary>
public sealed record OwnedGame
{
    public required long Id { get; init; }

    public required string Title { get; init; }

    public string? Slug { get; init; }

    /// <summary>
    /// Absolute, but extension-less: GOG expects a size suffix such as
    /// <c>_product_tile_256.jpg</c> to be appended by whoever renders it.
    /// </summary>
    public Uri? ImageBaseUrl { get; init; }

    public string? Category { get; init; }

    public DateTimeOffset? ReleasedAt { get; init; }

    public GamePlatforms Platforms { get; init; }

    public int DlcCount { get; init; }

    public bool IsHidden { get; init; }

    public bool IsGalaxyCompatible { get; init; }

    public bool IsInDevelopment { get; init; }

    /// <summary>Ids of the user's tags applied to this product.</summary>
    public IReadOnlyList<string> TagIds { get; init; } = [];

    /// <summary>GOG's community average rating in whole stars, 0–5. Zero means GOG has no rating
    /// for it.</summary>
    public int CommunityRating { get; init; }

    public bool SupportsCurrentPlatform => Platforms.HasFlag(CurrentPlatform);

    /// <summary>The <see cref="GamePlatforms"/> value matching the machine Goggles is running on.</summary>
    public static GamePlatforms CurrentPlatform =>
        OperatingSystem.IsWindows() ? GamePlatforms.Windows
        : OperatingSystem.IsMacOS() ? GamePlatforms.Mac
        : OperatingSystem.IsLinux() ? GamePlatforms.Linux
        : GamePlatforms.None;
}

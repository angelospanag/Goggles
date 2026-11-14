using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Goggles.Core.Library;
using Goggles.Desktop.Services;

namespace Goggles.Desktop.ViewModels;

/// <summary>One game in the library — backs both the grid tile and the list row.</summary>
public partial class GameTileViewModel : ViewModelBase
{
    private readonly OwnedGame _game;
    private readonly CoverImageCache _covers;

    [ObservableProperty]
    public partial Bitmap? Cover { get; set; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingStats { get; set; }

    [ObservableProperty]
    public partial bool HasAchievements { get; set; }

    [ObservableProperty]
    public partial int AchievementsUnlocked { get; set; }

    [ObservableProperty]
    public partial int AchievementsTotal { get; set; }

    [ObservableProperty]
    public partial bool HasPlaytime { get; set; }

    [ObservableProperty]
    public partial int PlaytimeMinutes { get; set; }

    public GameTileViewModel(OwnedGame game, CoverImageCache covers)
    {
        _game = game;
        _covers = covers;
    }

    public long Id => _game.Id;

    public string Title => _game.Title;

    public string? Slug => _game.Slug;

    /// <summary>GOG's community average rating in whole stars, 0–5; 0 means none.</summary>
    public int CommunityRating => _game.CommunityRating;

    /// <summary>The community rating drawn as five stars, e.g. "★★☆☆☆"; null when there is none.</summary>
    public string? RatingStars => CommunityRating is > 0 and <= 5
        ? new string('★', CommunityRating) + new string('☆', 5 - CommunityRating)
        : null;

    public string? RatingTooltip => RatingStars is null
        ? null
        : $"Community rating on GOG: {CommunityRating} out of 5";

    /// <summary>"—" while loading or when the game has no achievements.</summary>
    public string TrophyLabel => IsLoadingStats
        ? "…"
        : HasAchievements
            ? $"{(int)Math.Round(AchievementsUnlocked / (double)AchievementsTotal * 100)}%"
            : "—";

    /// <summary>"—" while loading or when playtime isn't available.</summary>
    public string TimeLabel
    {
        get
        {
            if (IsLoadingStats)
            {
                return "…";
            }

            if (!HasPlaytime)
            {
                return "—";
            }

            var hours = PlaytimeMinutes / 60;
            var minutes = PlaytimeMinutes % 60;
            return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
        }
    }

    public string Platforms => _game.Platforms == GamePlatforms.None
        ? "—"
        : _game.Platforms.ToString().Replace(", ", " · ");

    public bool SupportsCurrentPlatform => _game.SupportsCurrentPlatform;

    public GamePlatforms PlatformFlags => _game.Platforms;

    public string? Details => _game.DlcCount > 0 ? $"{_game.DlcCount} DLC" : null;

    public string? Category => _game.Category;

    public DateTimeOffset? ReleasedAt => _game.ReleasedAt;

    /// <summary>Position in GOG's own ordering, which is by purchase date, newest first.</summary>
    public int LibraryOrder { get; init; }

    /// <summary>True while the cover has not arrived, so the view can show a placeholder.</summary>
    public bool HasNoCover => Cover is null;

    public async Task LoadCoverAsync(CancellationToken cancellationToken = default)
    {
        // Started from the UI thread, so the continuation lands back on it and the binding is safe.
        Cover = await _covers.LoadAsync(_game.ImageBaseUrl, cancellationToken);
    }

    /// <summary>
    /// Fetches this game's achievement/playtime summary — lazy, only called once list view is
    /// opened (see <see cref="LibraryViewModel.StartLoadingStats"/>), not on every library refresh:
    /// this is a per-product call, and a 200+ game library means 200+ concurrent achievement/session
    /// fetches if it were eager. The cache's own throttle+TTL is what makes that safe, not free.
    /// </summary>
    public async Task LoadStatsAsync(
        GameStatsCache stats,
        long userId,
        CancellationToken cancellationToken = default)
    {
        IsLoadingStats = true;
        try
        {
            var result = await stats.GetStatsAsync(Id, userId, cancellationToken);
            HasAchievements = result.HasAchievements;
            AchievementsUnlocked = result.AchievementsUnlocked;
            AchievementsTotal = result.AchievementsTotal;
            HasPlaytime = result.HasPlaytime;
            PlaytimeMinutes = result.PlaytimeMinutes;
        }
        finally
        {
            IsLoadingStats = false;
        }
    }

    public bool Matches(string search) =>
        Title.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasNoCover));

    partial void OnIsLoadingStatsChanged(bool value)
    {
        OnPropertyChanged(nameof(TrophyLabel));
        OnPropertyChanged(nameof(TimeLabel));
    }

    partial void OnHasAchievementsChanged(bool value) => OnPropertyChanged(nameof(TrophyLabel));

    partial void OnAchievementsUnlockedChanged(int value) => OnPropertyChanged(nameof(TrophyLabel));

    partial void OnAchievementsTotalChanged(int value) => OnPropertyChanged(nameof(TrophyLabel));

    partial void OnHasPlaytimeChanged(bool value) => OnPropertyChanged(nameof(TimeLabel));

    partial void OnPlaytimeMinutesChanged(int value) => OnPropertyChanged(nameof(TimeLabel));
}

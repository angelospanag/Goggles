using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Goggles.Core.Api.Auth;
using Goggles.Core.Api.Embed;
using Goggles.Core.Downloads;
using Goggles.Core.Installs;
using Goggles.Core.Launching;
using Goggles.Core.Library;
using Goggles.Core.Manifests;
using Goggles.Desktop.Services;

namespace Goggles.Desktop.ViewModels;

/// <summary>The owned-games grid.</summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly LibrarySyncService _library;
    private readonly LibraryCache _cache;
    private readonly EmbedClient _embed;
    private readonly CoverImageCache _covers;
    private readonly GameStatsCache _stats;
    private readonly ITokenStore _tokens;
    private readonly ManifestResolver _manifests;
    private readonly DownloadEngine _downloads;
    private readonly string _installRoot;
    private readonly List<GameTileViewModel> _allGames = [];
    private readonly RunnerPreferences _runnerPreferences = new();

    /// <summary>False until the first sync, which is the one allowed to start from the cache.</summary>
    private bool _hasRefreshed;

    private CancellationTokenSource? _coverLoading;
    private CancellationTokenSource? _statsLoading;

    [ObservableProperty]
    public partial GameTileViewModel? SelectedGame { get; set; }

    [ObservableProperty]
    public partial GameDetailViewModel? Detail { get; set; }

    [ObservableProperty]
    public partial string? AccountName { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmingSignOut { get; set; }

    [ObservableProperty]
    public partial LibraryFilter Filter { get; set; } = LibraryFilter.All;

    [ObservableProperty]
    public partial int InstalledCount { get; set; }

    [ObservableProperty]
    public partial LibrarySort Sort { get; set; } = LibrarySort.Title;

    [ObservableProperty]
    public partial LibraryLayout Layout { get; set; } = LibraryLayout.Grid;

    /// <summary>Null means every genre; otherwise the one chosen from <see cref="Categories"/>.</summary>
    [ObservableProperty]
    public partial string? Category { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public LibraryViewModel(
        LibrarySyncService library,
        LibraryCache cache,
        EmbedClient embed,
        CoverImageCache covers,
        GameStatsCache stats,
        ITokenStore tokens,
        ManifestResolver manifests,
        DownloadEngine downloads,
        string installRoot)
    {
        _library = library;
        _cache = cache;
        _embed = embed;
        _covers = covers;
        _stats = stats;
        _tokens = tokens;
        _manifests = manifests;
        _downloads = downloads;
        _installRoot = installRoot;
    }

    /// <summary>Raised when the user asks to sign out; the shell owns what happens next.</summary>
    public event EventHandler? SignOutRequested;

    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    /// <summary>Genres actually present in this library, rather than a list GOG might change.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    public int PlayableCount => _allGames.Count(game => game.SupportsCurrentPlatform);

    public bool IsEmpty => !IsLoading && Games.Count == 0;

    /// <summary>
    /// Syncs the library. The first time, the cached copy from the last run is shown straight away and
    /// GOG is asked only whether it changed — one request when it has not. The Refresh button always
    /// fetches in full, since pressing it says "show me what GOG has now".
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Error = null;

        var userId = (await _tokens.LoadAsync())?.UserId;
        var cached = !_hasRefreshed && userId is not null ? await _cache.LoadAsync(userId) : null;
        var knownChecksum = _hasRefreshed ? null : _cache.ChecksumWorthTrusting(cached);
        _hasRefreshed = true;

        if (cached is not null)
        {
            Show(cached.Games, cached.AccountName);
            Status = $"{_allGames.Count} games · checking for changes…";
        }
        else
        {
            Status = "Syncing library…";
        }

        try
        {
            var result = await _library.SyncAsync(knownChecksum);
            AccountName = result.AccountName ?? AccountName;

            if (result.Changed)
            {
                Show(result.Games, result.AccountName);

                if (userId is not null)
                {
                    await SaveToCacheAsync(userId, result);
                }
            }

            Status = $"{_allGames.Count} games";
        }
        // A rejected session also raises AuthService.SignInRequired, which takes the shell back to
        // sign-in; the message here only shows for the moment before it does, or for failures that
        // signing in again would not fix. A library shown from the cache stays on screen meanwhile.
        catch (Exception exception) when (exception is HttpRequestException or Core.Api.GogApiException
                                              or GogAuthException)
        {
            Error = exception.Message;
            Status = _allGames.Count > 0 ? $"{_allGames.Count} games" : null;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Replaces the library on screen.</summary>
    private void Show(IReadOnlyList<OwnedGame> games, string? accountName)
    {
        AccountName = accountName ?? AccountName;

        // GOG returns the library newest-purchase first; keeping the index preserves that
        // ordering, which would otherwise be lost the moment the grid sorts by title.
        _allGames.Clear();
        _allGames.AddRange(games
            .Select((game, index) => new GameTileViewModel(game, _covers) { LibraryOrder = index }));

        Categories.Clear();
        foreach (var category in _allGames
                     .Select(game => game.Category)
                     .OfType<string>()
                     .Distinct()
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            Categories.Add(category);
        }

        RefreshInstalledState();
        ApplyFilter();
        OnPropertyChanged(nameof(PlayableCount));
        OnPropertyChanged(nameof(IsEmpty));

        StartLoadingCovers();
    }

    private async Task SaveToCacheAsync(string userId, LibrarySyncResult result)
    {
        try
        {
            await _cache.SaveAsync(userId, result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache only makes the next launch faster; failing to write it costs nothing today.
        }
    }

    public int NotInstalledCount => _allGames.Count - InstalledCount;

    [RelayCommand]
    private void SetFilter(LibraryFilter filter) => Filter = filter;

    /// <summary>
    /// Works out which games are on disk. One directory listing of the library root — no manifests,
    /// no network — because the marker file GOG ships inside each install carries the product id.
    /// </summary>
    private void RefreshInstalledState()
    {
        var installed = new GameLibrary(_installRoot)
            .Scan()
            .Select(game => game.ProductId)
            .ToHashSet();

        foreach (var game in _allGames)
        {
            game.IsInstalled = installed.Contains(game.Id);
        }

        InstalledCount = _allGames.Count(game => game.IsInstalled);
        OnPropertyChanged(nameof(NotInstalledCount));
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(LibraryFilter value) => ApplyFilter();

    partial void OnSortChanged(LibrarySort value) => ApplyFilter();

    partial void OnCategoryChanged(string? value) => ApplyFilter();

    [RelayCommand]
    private void SetSort(LibrarySort sort) => Sort = sort;

    [RelayCommand]
    private void ClearCategory() => Category = null;

    [RelayCommand]
    private void SetLayout(LibraryLayout layout) => Layout = layout;

    partial void OnLayoutChanged(LibraryLayout value)
    {
        if (value == LibraryLayout.List)
        {
            StartLoadingStats();
        }
    }

    partial void OnSelectedGameChanged(GameTileViewModel? value)
    {
        if (value is null)
        {
            Detail = null;
            return;
        }

        // Everything in the library is owned, which is exactly what depot filtering needs.
        var owned = _allGames.Select(game => game.Id).ToHashSet();
        var detail = new GameDetailViewModel(
            value, _manifests, _downloads, _installRoot, owned, _embed, _covers, _stats, _tokens, _runnerPreferences);

        detail.BackRequested += (_, _) => SelectedGame = null;

        // Installing or removing a game changes which filter it belongs in.
        detail.InstallStateChanged += (_, _) =>
        {
            RefreshInstalledState();
            ApplyFilter();
        };

        Detail = detail;

        // Resolving hits the network; the panel shows its own spinner while it does.
        _ = detail.LoadAsync();
    }

    [RelayCommand]
    private void ConfirmSignOut() => IsConfirmingSignOut = true;

    [RelayCommand]
    private void CancelSignOut() => IsConfirmingSignOut = false;

    /// <summary>
    /// Signing out only forgets the stored credentials — installed games stay where they are.
    /// </summary>
    [RelayCommand]
    private void SignOut()
    {
        IsConfirmingSignOut = false;
        SignOutRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Cancels the library-wide cover and stats loading. Called by the shell when this page is
    /// being left for sign-in, so hundreds of queued requests do not keep running against a
    /// session that is gone.
    /// </summary>
    public void StopBackgroundWork()
    {
        _coverLoading?.Cancel();
        _statsLoading?.Cancel();
    }

    private void ApplyFilter()
    {
        var matches = _allGames.Where(game => Filter switch
        {
            LibraryFilter.Installed => game.IsInstalled,
            LibraryFilter.NotInstalled => !game.IsInstalled,
            LibraryFilter.Playable => game.SupportsCurrentPlatform,
            _ => true,
        });

        if (Category is { Length: > 0 } category)
        {
            matches = matches.Where(game => game.Category == category);
        }

        if (Search.Trim() is { Length: > 0 } search)
        {
            matches = matches.Where(game => game.Matches(search));
        }

        matches = Sort switch
        {
            LibrarySort.RecentlyAdded => matches.OrderBy(game => game.LibraryOrder),
            LibrarySort.ReleaseDate => matches
                .OrderByDescending(game => game.ReleasedAt ?? DateTimeOffset.MinValue),
            _ => matches.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        };

        Games.Clear();
        foreach (var game in matches)
        {
            Games.Add(game);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Covers arrive after the grid does. They are fetched for the whole library rather than only
    /// what is on screen — the cache throttles the requests, and a second launch reads from disk.
    /// </summary>
    private void StartLoadingCovers()
    {
        _coverLoading?.Cancel();
        _coverLoading?.Dispose();
        _coverLoading = new CancellationTokenSource();

        var token = _coverLoading.Token;

        foreach (var game in _allGames)
        {
            _ = LoadCoverAsync(game, token);
        }
    }

    private static async Task LoadCoverAsync(GameTileViewModel game, CancellationToken token)
    {
        try
        {
            await game.LoadCoverAsync(token);
        }
        catch (OperationCanceledException)
        {
            // A refresh replaced this load; nothing to do.
        }
    }

    /// <summary>
    /// Fetches achievement/playtime stats for the whole (filtered) library — but only once list
    /// view is actually opened (see <see cref="OnLayoutChanged"/>), unlike covers, which load
    /// unconditionally. Achievements/sessions are a per-product API call to an undocumented host,
    /// so this stays opt-in; the cache's throttle+TTL make repeat visits cheap, not the initial one.
    /// </summary>
    private void StartLoadingStats()
    {
        _statsLoading?.Cancel();
        _statsLoading?.Dispose();
        _statsLoading = new CancellationTokenSource();

        var token = _statsLoading.Token;

        _ = LoadStatsForLibraryAsync(token);
    }

    private async Task LoadStatsForLibraryAsync(CancellationToken token)
    {
        var tokens = await _tokens.LoadAsync(token);
        if (tokens?.UserId is not { } userId || !long.TryParse(userId, out var parsedUserId))
        {
            return;
        }

        // Fired concurrently, same as StartLoadingCovers — GameStatsCache's own throttle bounds
        // how many are actually in flight at once.
        foreach (var game in _allGames)
        {
            _ = LoadStatsAsync(game, _stats, parsedUserId, token);
        }
    }

    private static async Task LoadStatsAsync(
        GameTileViewModel game,
        GameStatsCache stats,
        long userId,
        CancellationToken token)
    {
        try
        {
            await game.LoadStatsAsync(stats, userId, token);
        }
        catch (OperationCanceledException)
        {
            // A layout switch or refresh replaced this load; nothing to do.
        }
    }
}

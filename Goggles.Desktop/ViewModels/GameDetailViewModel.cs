using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Goggles.Core.Api;
using Goggles.Core.Api.Auth;
using Goggles.Core.Api.Embed;
using Goggles.Core.Api.Embed.Models;
using Goggles.Core.Api.Gameplay.Models;
using Goggles.Core.Downloads;
using Goggles.Core.Installs;
using Goggles.Core.Launching;
using Goggles.Core.Library;
using Goggles.Core.Manifests;
using Goggles.Desktop.Services;

namespace Goggles.Desktop.ViewModels;

/// <summary>Full-page detail view for the selected game: hero art, activity, and the install
/// itself.</summary>
public partial class GameDetailViewModel : ViewModelBase
{
    /// <summary>How often the progress bar is allowed to move, in milliseconds.</summary>
    private const long ProgressIntervalMs = 50;

    private readonly ManifestResolver _manifests;
    private readonly DownloadEngine _downloads;
    private readonly string _installRoot;
    private readonly EmbedClient _embed;
    private readonly CoverImageCache _covers;
    private readonly GameStatsCache _stats;
    private readonly ITokenStore _tokens;
    private readonly Stopwatch _sinceLastProgress = new();

    private CancellationTokenSource? _cancellation;
    private ResolvedBuild? _build;
    private LaunchTarget? _launchTarget;
    private IReadOnlyList<Achievement> _allAchievementsRaw = [];

    [ObservableProperty]
    public partial DetailTab ActiveTab { get; set; } = DetailTab.Overview;

    [ObservableProperty]
    public partial Bitmap? Hero { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingActivity { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingProgress { get; set; }

    [ObservableProperty]
    public partial int AchievementsUnlocked { get; set; }

    [ObservableProperty]
    public partial int AchievementsTotal { get; set; }

    public bool HasAnyAchievements => AchievementsTotal > 0;

    partial void OnAchievementsTotalChanged(int value) => OnPropertyChanged(nameof(HasAnyAchievements));

    [ObservableProperty]
    public partial bool IsResolving { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; }

    [ObservableProperty]
    public partial string? BuildLabel { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    public partial bool CanPlay { get; set; }

    /// <summary>Name of the play task, shown beside the button rather than on it.</summary>
    [ObservableProperty]
    public partial string? PlayLabel { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmingUninstall { get; set; }

    [ObservableProperty]
    public partial string? InstalledSize { get; set; }

    private readonly IReadOnlySet<long> _ownedProductIds;
    private readonly RunnerPreferences _runnerPreferences;

    /// <summary>Set while the runner list is being filled, so preselecting one is not mistaken
    /// for the user choosing it.</summary>
    private bool _isLoadingRunners;

    [ObservableProperty]
    public partial WindowsRunner? SelectedRunner { get; set; }

    public GameDetailViewModel(
        GameTileViewModel tile,
        ManifestResolver manifests,
        DownloadEngine downloads,
        string installRoot,
        IReadOnlySet<long> ownedProductIds,
        EmbedClient embed,
        CoverImageCache covers,
        GameStatsCache stats,
        ITokenStore tokens,
        RunnerPreferences runnerPreferences)
    {
        Tile = tile;
        _manifests = manifests;
        _downloads = downloads;
        _installRoot = installRoot;
        _ownedProductIds = ownedProductIds;
        _embed = embed;
        _covers = covers;
        _stats = stats;
        _tokens = tokens;
        _runnerPreferences = runnerPreferences;
    }

    /// <summary>Raised after an install or an uninstall, so the library can restate what is on disk.</summary>
    public event EventHandler? InstallStateChanged;

    /// <summary>Raised when the back arrow is pressed; the library owns returning to itself
    /// (same event-based pattern as <see cref="InstallStateChanged"/> and
    /// <see cref="LibraryViewModel.SignOutRequested"/>).</summary>
    public event EventHandler? BackRequested;

    public GameTileViewModel Tile { get; }

    public string Title => Tile.Title;

    public string Platforms => Tile.Platforms;

    public string? RatingStars => Tile.RatingStars;

    public string? RatingTooltip => Tile.RatingTooltip;

    public ObservableCollection<AchievementActivityGroup> ActivityGroups { get; } = [];

    /// <summary>The "My progress" tab's full achievement list — locked and unlocked. Built lazily
    /// the first time that tab is opened (see <see cref="OnActiveTabChanged"/>), not alongside the
    /// Overview tab's activity feed: fetching every achievement's icon (not just unlocked ones)
    /// is real extra cost that shouldn't be paid unless the tab is actually viewed.</summary>
    public ObservableCollection<AchievementProgressItem> AllAchievements { get; } = [];

    partial void OnActiveTabChanged(DetailTab value)
    {
        if (value == DetailTab.Progress && AllAchievements.Count == 0 && _allAchievementsRaw.Count > 0)
        {
            _ = BuildProgressListAsync();
        }
    }

    [ObservableProperty]
    public partial bool HasActivity { get; set; }

    /// <summary>Goodies for this game and for any DLC of it the account owns.</summary>
    public ObservableCollection<GameExtraItem> Extras { get; } = [];

    /// <summary>Titles of the DLC the account owns — gameDetails lists no others.</summary>
    public ObservableCollection<string> OwnedDlcs { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingDetails { get; set; }

    [ObservableProperty]
    public partial bool HasExtras { get; set; }

    [ObservableProperty]
    public partial bool HasOwnedDlcs { get; set; }

    [ObservableProperty]
    public partial Uri? ForumLink { get; set; }

    /// <summary>Where goodies are saved: one folder per game under the user's Downloads, since the
    /// library root holds exactly what GOG's builds produce and nothing else.</summary>
    public string ExtrasDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "Goggles",
        string.Concat(Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim());

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SetTab(DetailTab tab) => ActiveTab = tab;

    /// <summary>Opens the game's real GOG store page in the system's default browser — same
    /// pattern <see cref="LoginViewModel"/> already uses to open the sign-in URL.</summary>
    [RelayCommand]
    private void OpenStorePage()
    {
        if (Tile.Slug is not { Length: > 0 } slug)
        {
            return;
        }

        var url = $"https://www.gog.com/game/{slug}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    [RelayCommand]
    private void OpenForum()
    {
        if (ForumLink is { } link)
        {
            Process.Start(new ProcessStartInfo(link.ToString()) { UseShellExecute = true })?.Dispose();
        }
    }

    public string? InstallPath { get; private set; }

    /// <summary>
    /// Whether the game was already on disk when this panel opened — distinct from
    /// <see cref="IsInstalled"/>, which also becomes true the moment an install finishes.
    /// </summary>
    public bool WasInstalledBefore { get; private set; }

    public string PlayTaskDescription => PlayLabel is { Length: > 0 } name ? $"Runs {name}" : string.Empty;

    /// <summary>
    /// GOG serves a build per platform. Prefer the one this machine can actually run; fall back to
    /// Windows, which is downloadable even though Goggles cannot launch it yet.
    /// </summary>
    public string TargetOs { get; private set; } = "windows";

    /// <summary>
    /// A Windows build on macOS or Linux, which only starts through Wine or CrossOver.
    /// </summary>
    public bool NeedsRunner => TargetOs == "windows" && !OperatingSystem.IsWindows();

    /// <summary>The Wine and CrossOver setups found on this machine.</summary>
    public ObservableCollection<WindowsRunner> Runners { get; } = [];

    /// <summary>A Windows build with nothing installed to run it.</summary>
    public bool HasNoRunner => NeedsRunner && Runners.Count == 0;

    /// <summary>Resolves the build so the panel can say what installing would cost.</summary>
    public async Task LoadAsync()
    {
        IsResolving = true;
        Error = null;
        Summary = null;
        BuildLabel = null;
        ActiveTab = DetailTab.Overview;
        Hero = null;
        ActivityGroups.Clear();
        HasActivity = false;
        AllAchievements.Clear();
        _allAchievementsRaw = [];
        AchievementsUnlocked = 0;
        AchievementsTotal = 0;
        Extras.Clear();
        OwnedDlcs.Clear();
        HasExtras = false;
        HasOwnedDlcs = false;
        ForumLink = null;

        // Independent of the build resolve below, and of each other — a slow/failing details fetch
        // must never prevent the activity feed from loading, or vice versa.
        _ = LoadDetailsAsync();
        _ = LoadActivityAsync();

        try
        {
            TargetOs = ChooseOs(Tile.PlatformFlags);
            LoadRunners();
            _build = await _manifests.ResolveAsync(
                Tile.Id,
                new ResolveOptions { Os = TargetOs, OwnedProductIds = _ownedProductIds });

            var installPath = new GameLibrary(_installRoot).InstallPathFor(Tile.Id, _build.InstallDirectory);

            InstallPath = installPath;
            IsInstalled = GameLibrary.Contains(installPath, Tile.Id);
            WasInstalledBefore = IsInstalled;

            await RefreshLaunchTargetAsync();

            Summary =
                $"{_build.Files.Count:N0} files · {Bytes(_build.TotalDownloadSize)} to download · " +
                $"{Bytes(_build.TotalSize)} installed";
            BuildLabel = $"Build {_build.BuildId} · {TargetOs}";

            OnPropertyChanged(nameof(InstallPath));
            OnPropertyChanged(nameof(WasInstalledBefore));
            InstallCommand.NotifyCanExecuteChanged();
        }
        // LoadAsync runs fire-and-forget, so anything not caught here is lost silently. A rejected
        // session lands here too: it shows as an error while AuthService.SignInRequired takes the
        // shell back to sign-in.
        catch (Exception exception) when (exception is GogApiException or GogAuthException
                                              or HttpRequestException or NotSupportedException
                                              or InvalidDataException)
        {
            Error = exception.Message;
        }
        finally
        {
            IsResolving = false;
        }
    }

    private bool CanInstall => _build is not null && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_build is null || InstallPath is not { } installPath)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsDownloading = true;
        Error = null;
        Progress = 0;
        ProgressText = "Starting…";
        _sinceLastProgress.Restart();

        try
        {
            // Progress<T> hops back to the UI thread for us, so the handler can touch bindings.
            var report = await _downloads.DownloadAsync(
                _build,
                installPath,
                new Progress<DownloadProgress>(OnProgress),
                _cancellation.Token);

            Progress = 1;
            ProgressText = report.ChunksDownloaded == 0
                ? "Already up to date"
                : $"Installed {Bytes(report.BytesWritten)} in {report.Duration.TotalSeconds:N1}s";
            IsInstalled = true;
            await RefreshLaunchTargetAsync();
            InstallStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled — resume by installing again";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
                                              or IOException or GogApiException or GogAuthException)
        {
            Error = exception.Message;
            ProgressText = null;
        }
        finally
        {
            IsDownloading = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (_launchTarget is null)
        {
            return;
        }

        try
        {
            // Off the UI thread: starting a process can block briefly, and the window should not.
            var target = _launchTarget;
            var runner = NeedsRunner ? SelectedRunner : null;
            await Task.Run(() => new GameLauncher().Launch(target, runner));
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            Error = $"Could not start the game: {exception.Message}";
        }
    }

    /// <summary>
    /// Finds the installed runners and preselects this game's remembered one — or the last one
    /// picked anywhere, so a library of Windows games does not ask the same question every time.
    /// </summary>
    private void LoadRunners()
    {
        _isLoadingRunners = true;
        try
        {
            Runners.Clear();

            if (NeedsRunner)
            {
                foreach (var runner in WindowsRunnerDiscovery.Find())
                {
                    Runners.Add(runner);
                }
            }

            SelectedRunner = NeedsRunner ? _runnerPreferences.Choose(Tile.Id, Runners) : null;
        }
        finally
        {
            _isLoadingRunners = false;
        }

        OnPropertyChanged(nameof(NeedsRunner));
        OnPropertyChanged(nameof(HasNoRunner));
        UpdateCanPlay();
    }

    partial void OnSelectedRunnerChanged(WindowsRunner? value)
    {
        if (value is not null && !_isLoadingRunners)
        {
            _runnerPreferences.Remember(Tile.Id, value);
        }

        UpdateCanPlay();
    }

    /// <summary>Play needs a resolved play task, and for a Windows build, something to run it with.</summary>
    private void UpdateCanPlay() =>
        CanPlay = _launchTarget is not null && (!NeedsRunner || SelectedRunner is not null);

    [RelayCommand]
    private void ConfirmUninstall() => IsConfirmingUninstall = true;

    [RelayCommand]
    private void CancelUninstall() => IsConfirmingUninstall = false;

    /// <summary>
    /// Deletes the install. Guarded in <see cref="GameUninstaller"/> rather than here, so the CLI
    /// gets the same protection.
    /// </summary>
    [RelayCommand]
    private async Task UninstallAsync()
    {
        IsConfirmingUninstall = false;

        if (InstallPath is null)
        {
            return;
        }

        try
        {
            var result = await Task.Run(
                () => new GameUninstaller().Uninstall(_installRoot, InstallPath, Tile.Id));

            IsInstalled = false;
            WasInstalledBefore = false;
            CanPlay = false;
            PlayLabel = null;
            _launchTarget = null;
            Progress = 0;
            InstalledSize = null;
            ProgressText = result.Removed ? $"Uninstalled — freed {Bytes(result.BytesFreed)}" : "Nothing to remove";

            OnPropertyChanged(nameof(WasInstalledBefore));
            InstallStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                              or UnauthorizedAccessException)
        {
            Error = $"Could not uninstall: {exception.Message}";
        }
    }

    /// <summary>
    /// Reads the play tasks GOG ships inside the install. Absent or unreadable ones only cost the
    /// Play button, so they are not surfaced as errors.
    /// </summary>
    private async Task RefreshLaunchTargetAsync()
    {
        _launchTarget = null;
        CanPlay = false;
        PlayLabel = null;

        if (!IsInstalled || InstallPath is not { } installPath)
        {
            InstalledSize = null;
            return;
        }

        try
        {
            _launchTarget = await GameLauncher.ResolveTargetAsync(installPath, Tile.Id);
            UpdateCanPlay();
            PlayLabel = _launchTarget?.Name;
            InstalledSize = Bytes(await Task.Run(() => GameUninstaller.Measure(installPath)));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            CanPlay = false;
        }
    }

    /// <summary>
    /// Fetches gameDetails once and fills everything that comes from it: the hero image, the
    /// goodies, owned DLC and the forum link. Independent of <see cref="LoadActivityAsync"/> and the
    /// install/build resolve in <see cref="LoadAsync"/> — never surfaces its own errors, since none
    /// of this is a reason to block the rest of the page. The catch-all is deliberate: this runs
    /// unawaited (see <see cref="LoadAsync"/>), so any exception that escaped here would simply
    /// vanish as an unobserved task exception, leaving the page in its correct empty state.
    /// </summary>
    private async Task LoadDetailsAsync()
    {
        IsLoadingDetails = true;
        try
        {
            var details = await _embed.GetGameDetailsAsync(Tile.Id);
            if (details is null)
            {
                return;
            }

            ForumLink = GogLink(details.ForumLink);

            foreach (var extra in details.Extras)
            {
                AddExtra(extra, from: null);
            }

            foreach (var dlc in details.Dlcs)
            {
                if (dlc.Title is { Length: > 0 } dlcTitle)
                {
                    OwnedDlcs.Add(dlcTitle);
                }

                foreach (var extra in dlc.Extras)
                {
                    AddExtra(extra, from: dlc.Title);
                }
            }

            HasExtras = Extras.Count > 0;
            HasOwnedDlcs = OwnedDlcs.Count > 0;

            if (details.BackgroundImage is { Length: > 0 } backgroundImage &&
                Uri.TryCreate(NormalizeImageUrl(backgroundImage) + ".jpg", UriKind.Absolute, out var heroUri))
            {
                Hero = await _covers.LoadRawAsync(heroUri);
            }
        }
        catch (Exception exception)
        {
            // Logged rather than fully silent, since this runs unawaited and would otherwise
            // vanish untraceably.
            Console.Error.WriteLine($"[GameDetailViewModel] game details failed for {Tile.Id}: {exception}");
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    private void AddExtra(GameExtra extra, string? from)
    {
        if (extra.ManualUrl is { Length: > 0 })
        {
            Extras.Add(new GameExtraItem(_embed, extra, from, ExtrasDirectory));
        }
    }

    /// <summary>A link from GOG's JSON, accepted only if it is https and stays on gog.com — it is
    /// handed to the system to open.</summary>
    private static Uri? GogLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "gog.com" || uri.Host.EndsWith(".gog.com", StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;

    /// <summary>
    /// Makes GOG's protocol-relative image URLs usable — the same fix
    /// <c>LibrarySyncService.NormaliseImageUrl</c> applies to tile art, needed again here since
    /// <c>GameDetails.BackgroundImage</c> comes from the same embed.gog.com dialect but isn't routed
    /// through that mapper. Confirmed live: without this, <see cref="Uri.TryCreate"/> silently
    /// resolves a leading "//" to the "file" scheme instead of failing cleanly, and the fetch dies
    /// with "The 'file' scheme is not supported." <see cref="LoadDetailsAsync"/> also appends ".jpg" to
    /// the result — despite looking like a complete URL, BackgroundImage is just as extension-less
    /// as tile art, and GOG's image CDN 404s a bare hash with no suffix at all (confirmed live); an
    /// unqualified ".jpg" with no size token serves the image at its full native resolution.
    /// </summary>
    private static string NormalizeImageUrl(string image) =>
        image.StartsWith("//", StringComparison.Ordinal) ? $"https:{image}" : image;

    /// <summary>
    /// Fetches the achievement-unlock activity feed. Independent of <see cref="LoadDetailsAsync"/> —
    /// see its remarks for why the catch-all here is deliberate too.
    /// </summary>
    private async Task LoadActivityAsync()
    {
        IsLoadingActivity = true;
        try
        {
            var tokens = await _tokens.LoadAsync();
            if (tokens?.UserId is not { } userId || !long.TryParse(userId, out var parsedUserId))
            {
                return;
            }

            var stats = await _stats.GetStatsAsync(Tile.Id, parsedUserId);
            await BuildActivityGroupsAsync(stats.UnlockedAchievements);
            HasActivity = ActivityGroups.Count > 0;

            _allAchievementsRaw = stats.AllAchievements;
            AchievementsUnlocked = stats.AchievementsUnlocked;
            AchievementsTotal = stats.AchievementsTotal;

            // The user may already be sitting on the Progress tab (e.g. switched there while this
            // was still loading) — OnActiveTabChanged only fires on a *change*, so build it here too.
            if (ActiveTab == DetailTab.Progress && AllAchievements.Count == 0)
            {
                await BuildProgressListAsync();
            }
        }
        catch (Exception exception)
        {
            // No activity feed this session; the page still works without one. Logged rather than
            // fully silent, since this runs unawaited and would otherwise vanish untraceably.
            Console.Error.WriteLine($"[GameDetailViewModel] activity feed failed for {Tile.Id}: {exception}");
        }
        finally
        {
            IsLoadingActivity = false;
        }
    }

    private async Task BuildActivityGroupsAsync(IReadOnlyList<Achievement> unlocked)
    {
        var groups = unlocked
            .Where(a => a.UnlockedAt is not null)
            .GroupBy(a => RelativeTime.DateKey(a.UnlockedAt!.Value))
            .OrderByDescending(group => group.Key);

        foreach (var group in groups)
        {
            var items = await Task.WhenAll(group.Select(async achievement =>
            {
                Bitmap? icon = null;
                if (achievement.ImageUrlUnlocked is { Length: > 0 } iconUrl &&
                    Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri))
                {
                    icon = await _covers.LoadRawAsync(iconUri);
                }

                return new AchievementActivityItem(achievement.Name ?? achievement.Key, icon);
            }));

            var count = items.Length;
            var summary = $"Earned {count} achievement{(count == 1 ? "" : "s")}";
            var relative = RelativeTime.Format(group.First().UnlockedAt!.Value);

            ActivityGroups.Add(new AchievementActivityGroup(summary, relative, items));
        }
    }

    /// <summary>
    /// Builds the "My progress" tab's full achievement list, fetching an icon for every entry —
    /// not just unlocked ones, unlike <see cref="BuildActivityGroupsAsync"/>. Never surfaces its
    /// own errors, for the same reason <see cref="LoadDetailsAsync"/>/<see cref="LoadActivityAsync"/>
    /// don't: it runs unawaited from <see cref="OnActiveTabChanged"/>.
    /// </summary>
    private async Task BuildProgressListAsync()
    {
        IsLoadingProgress = true;
        try
        {
            var items = await Task.WhenAll(_allAchievementsRaw.Select(async achievement =>
            {
                var unlocked = achievement.UnlockedAt is not null;
                var secret = !unlocked && !achievement.Visible;

                if (secret)
                {
                    return new AchievementProgressItem(
                        Name: "Hidden achievement",
                        Description: null,
                        Icon: null,
                        IsUnlocked: false,
                        IsSecret: true,
                        RelativeUnlockedAt: null);
                }

                var iconUrl = unlocked ? achievement.ImageUrlUnlocked : achievement.ImageUrlLocked;
                Bitmap? icon = null;
                if (iconUrl is { Length: > 0 } && Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri))
                {
                    icon = await _covers.LoadRawAsync(iconUri);
                }

                return new AchievementProgressItem(
                    Name: achievement.Name ?? achievement.Key,
                    Description: achievement.Description,
                    Icon: icon,
                    IsUnlocked: unlocked,
                    IsSecret: false,
                    RelativeUnlockedAt: unlocked ? RelativeTime.Format(achievement.UnlockedAt!.Value) : null);
            }));

            foreach (var item in items)
            {
                AllAchievements.Add(item);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[GameDetailViewModel] progress list failed for {Tile.Id}: {exception}");
        }
        finally
        {
            IsLoadingProgress = false;
        }
    }

    /// <summary>
    /// Throttled: a large build reports thousands of chunks, and repainting for each one would cost
    /// more than the download.
    /// </summary>
    private void OnProgress(DownloadProgress progress)
    {
        var finished = progress.ChunksCompleted == progress.TotalChunks;

        if (!finished && _sinceLastProgress.ElapsedMilliseconds < ProgressIntervalMs)
        {
            return;
        }

        _sinceLastProgress.Restart();
        Progress = progress.Fraction;
        ProgressText =
            $"{progress.Fraction:P0} · {progress.ChunksCompleted:N0}/{progress.TotalChunks:N0} chunks · " +
            $"{Bytes(progress.BytesCompleted)} of {Bytes(progress.TotalBytes)}";
    }

    private static string ChooseOs(GamePlatforms platforms)
    {
        if (OperatingSystem.IsMacOS() && platforms.HasFlag(GamePlatforms.Mac))
        {
            return "osx";
        }

        if (OperatingSystem.IsLinux() && platforms.HasFlag(GamePlatforms.Linux))
        {
            return "linux";
        }

        return platforms.HasFlag(GamePlatforms.Windows) ? "windows"
            : platforms.HasFlag(GamePlatforms.Mac) ? "osx"
            : "linux";
    }

    private static string Bytes(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):N2} GiB",
        >= 1L << 20 => $"{value / (double)(1L << 20):N1} MiB",
        >= 1L << 10 => $"{value / (double)(1L << 10):N0} KiB",
        _ => $"{value} B",
    };

    partial void OnIsDownloadingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();

    partial void OnPlayLabelChanged(string? value) => OnPropertyChanged(nameof(PlayTaskDescription));
}

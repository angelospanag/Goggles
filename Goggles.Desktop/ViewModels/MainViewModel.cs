using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Goggles.Core;
using Goggles.Desktop.Services;

namespace Goggles.Desktop.ViewModels;

/// <summary>
/// The shell: shows sign-in until there are credentials, then the library.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const string SessionExpiredMessage = "Your GOG session has expired. Sign in again to continue.";

    private readonly GogSession _session;
    private readonly CoverImageCache _covers;

    /// <summary>
    /// Set while a sign-out is in progress, so the burst of <see cref="Core.Api.Auth.AuthService.SignInRequired"/>
    /// events that in-flight requests raise as the session goes away cannot start a second one.
    /// </summary>
    private bool _isSigningOut;

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    public MainViewModel(GogSession session, CoverImageCache covers)
    {
        _session = session;
        _covers = covers;

        // One subscription for the whole app, rather than every screen catching its own auth
        // failures: whichever request first finds the session rejected, the answer is the same.
        _session.Auth.SignInRequired += OnSignInRequired;
    }

    /// <summary>
    /// Decides the opening screen. Stored credentials are trusted enough to show the library — an
    /// expired token refreshes itself, and one GOG rejects sends the user back here to sign in.
    /// </summary>
    public async Task StartAsync()
    {
        if (await _session.Tokens.LoadAsync() is not null)
        {
            await ShowLibraryAsync();
            return;
        }

        ShowLogin();
    }

    private void ShowLogin(string? message = null)
    {
        var login = new LoginViewModel(_session.Auth) { Error = message };
        login.SignedIn += async (_, _) => await ShowLibraryAsync();

        CurrentPage = login;
    }

    private async Task ShowLibraryAsync()
    {
        var library = new LibraryViewModel(
            _session.Library,
            _session.Embed,
            _covers,
            _session.Stats,
            _session.Tokens,
            _session.Manifests,
            _session.Downloads,
            DefaultInstallRoot);

        library.SignOutRequested += async (_, _) => await SignOutAsync();
        CurrentPage = library;

        // Through the command rather than RefreshAsync directly, so the Refresh button stays
        // disabled until this first sync finishes instead of starting a second one alongside it.
        await library.RefreshCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Raised from whichever thread noticed, possibly many times for one expiry, and possibly by a
    /// request still finishing after the user already signed out — so it is marshalled to the UI
    /// thread and ignored unless the library is actually showing.
    /// </summary>
    private void OnSignInRequired(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (_isSigningOut || CurrentPage is not LibraryViewModel)
            {
                return;
            }

            await SignOutAsync(SessionExpiredMessage);
        });

    /// <summary>
    /// Forgets the stored credentials and returns to sign-in. Installed games are left alone —
    /// signing out of an account is not a reason to delete someone's downloads.
    /// </summary>
    private async Task SignOutAsync(string? message = null)
    {
        if (_isSigningOut)
        {
            return;
        }

        _isSigningOut = true;
        try
        {
            if (CurrentPage is LibraryViewModel library)
            {
                library.StopBackgroundWork();
            }

            await _session.Auth.SignOutAsync();
            ShowLogin(message);
        }
        finally
        {
            _isSigningOut = false;
        }
    }

    /// <summary>Where games land. A configurable library folder is a settings screen away.</summary>
    private static string DefaultInstallRoot => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "Games",
        "Goggles");
}

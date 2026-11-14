using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Goggles.Core.Api.Auth;

namespace Goggles.Desktop.ViewModels;

/// <summary>
/// Sign-in. On macOS the view embeds the system web view and hands the post-login redirect to
/// <see cref="CompleteAsync"/>. Elsewhere — and as the way out when GOG serves a CAPTCHA the
/// embedded browser cannot handle — this opens the system browser and asks for the address it
/// lands on.
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;

    [ObservableProperty]
    public partial string RedirectUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Host of the page the embedded browser is showing, so the user can see whose it is.</summary>
    [ObservableProperty]
    public partial string? Host { get; set; }

    [ObservableProperty]
    public partial bool IsPageLoading { get; set; }

    public LoginViewModel(AuthService auth)
    {
        _auth = auth;
        AuthorizationUrl = auth.BuildAuthorizationUrl();
    }

    /// <summary>Raised once credentials are stored.</summary>
    public event EventHandler? SignedIn;

    public Uri AuthorizationUrl { get; }

    /// <summary>
    /// Whether a navigation is the post-login redirect carrying an authorization code. Lets the
    /// embedded browser tell "the user finished signing in" from every other page GOG serves.
    /// </summary>
    public bool IsRedirect(Uri url) => _auth.TryExtractCode(url, out _);

    /// <summary>Completes sign-in from a redirect the embedded browser landed on.</summary>
    public async Task CompleteAsync(Uri redirect)
    {
        if (IsBusy || !_auth.TryExtractCode(redirect, out var code))
        {
            return;
        }

        RedirectUrl = redirect.ToString();
        await ExchangeAsync(code!);
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AuthorizationUrl.ToString()) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            Error = $"Could not open a browser ({exception.Message}). Copy the address below instead.";
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        var pasted = RedirectUrl.Trim();

        if (pasted.Length == 0)
        {
            Error = "Paste the address you were redirected to after signing in.";
            return;
        }

        // People paste the whole redirect URL or just the code; accept either.
        string code;
        if (Uri.TryCreate(pasted, UriKind.Absolute, out var redirect))
        {
            if (!_auth.TryExtractCode(redirect, out var extracted))
            {
                Error = "That address has no authorization code in it.";
                return;
            }

            code = extracted!;
        }
        else
        {
            code = pasted;
        }

        await ExchangeAsync(code);
    }

    private async Task ExchangeAsync(string code)
    {
        IsBusy = true;
        Error = null;

        try
        {
            await _auth.ExchangeCodeAsync(code);
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (GogAuthException exception)
        {
            Error = $"GOG rejected the sign-in: {exception.Message}";
        }
        catch (HttpRequestException exception)
        {
            Error = $"Could not reach GOG: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

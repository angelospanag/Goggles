using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Goggles.Desktop.Platform;
using Goggles.Desktop.ViewModels;

namespace Goggles.Desktop.Views;

public partial class LoginView : UserControl
{
    /// <summary>
    /// How often the embedded browser's address is checked. GOG's redirect is the only navigation
    /// that matters, and a fifth of a second is imperceptible next to a sign-in.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _watchAddress;

    public LoginView()
    {
        InitializeComponent();

        _watchAddress = new DispatcherTimer { Interval = PollInterval };
        _watchAddress.Tick += OnCheckAddress;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!MacWebView.IsSupported || DataContext is not LoginViewModel viewModel)
        {
            return;
        }

        // Where the OS provides a web view, sign-in happens in the window; the paste flow stays as
        // the way out if GOG serves something the embedded browser cannot handle.
        Browser.IsVisible = true;
        AddressBar.IsVisible = true;
        FallbackStrip.IsVisible = true;
        PasteFallback.IsVisible = false;

        viewModel.IsPageLoading = true;
        Browser.UseSafariUserAgent();
        Browser.Navigate(viewModel.AuthorizationUrl);
        _watchAddress.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _watchAddress.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnCheckAddress(object? sender, EventArgs e)
    {
        if (DataContext is not LoginViewModel viewModel || Browser.CurrentUrl is not { } url)
        {
            return;
        }

        // The first address means WebKit has something to show.
        viewModel.IsPageLoading = false;
        viewModel.Host = url.Host;

        if (!viewModel.IsRedirect(url))
        {
            return;
        }

        // The code is single use, so stop watching before handing it over.
        _watchAddress.Stop();
        await viewModel.CompleteAsync(url);
    }

    /// <summary>
    /// Swaps to the paste flow, for whatever the embedded browser turns out not to handle — a
    /// CAPTCHA being the likeliest.
    /// </summary>
    private void OnShowPasteFallback(object? sender, RoutedEventArgs e)
    {
        _watchAddress.Stop();

        Browser.IsVisible = false;
        AddressBar.IsVisible = false;
        FallbackStrip.IsVisible = false;
        PasteFallback.IsVisible = true;

        if (DataContext is LoginViewModel viewModel)
        {
            viewModel.IsPageLoading = false;
        }
    }

    /// <summary>
    /// Reads the redirect URL straight from the clipboard and signs in, saving the user a paste.
    /// </summary>
    /// <remarks>
    /// The clipboard belongs to the window, not to the view model, so this stays in the view rather
    /// than becoming a service abstraction with exactly one implementation.
    /// </remarks>
    private async void OnPasteAndSignIn(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel viewModel)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            viewModel.Error = "Could not read the clipboard; paste the address into the box instead.";
            return;
        }

        if (await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            viewModel.Error = "The clipboard is empty. Copy the address from your browser first.";
            return;
        }

        viewModel.RedirectUrl = text.Trim();
        await viewModel.SignInCommand.ExecuteAsync(null);
    }
}

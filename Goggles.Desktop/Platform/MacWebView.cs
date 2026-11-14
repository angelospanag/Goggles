using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using static Goggles.Desktop.Platform.ObjectiveC;

namespace Goggles.Desktop.Platform;

/// <summary>
/// A WKWebView embedded through Avalonia's <see cref="NativeControlHost"/>, so GOG's sign-in page
/// can be shown inside the app.
/// </summary>
/// <remarks>
/// GOG validates <c>redirect_uri</c>, so the authorization code can only be captured by watching a
/// browser navigate to Galaxy's registered redirect. Avalonia ships no browser, and the packages
/// that provide one bundle Chromium — which is what avoiding Electron was meant to prevent. So this
/// hosts the operating system's own web view instead, through the Objective-C runtime.
/// <para>
/// Deliberately tiny: load a URL and read the current one. Navigation is observed by polling rather
/// than by implementing a <c>WKNavigationDelegate</c>, which would mean synthesising an
/// Objective-C class at runtime for no benefit here.
/// </para>
/// </remarks>
public sealed class MacWebView : NativeControlHost
{
    private const string WebKitFramework = "/System/Library/Frameworks/WebKit.framework/WebKit";

    /// <summary>NSViewWidthSizable | NSViewHeightSizable.</summary>
    private const int Resizable = 2 | 16;

    private IntPtr _webView;
    private Uri? _pending;

    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>The page currently shown, or null before anything has loaded.</summary>
    public Uri? CurrentUrl
    {
        get
        {
            if (_webView == IntPtr.Zero)
            {
                return null;
            }

            var url = Send(_webView, Selector("URL"));
            if (url == IntPtr.Zero)
            {
                return null;
            }

            var text = ReadString(Send(url, Selector("absoluteString")));

            return Uri.TryCreate(text, UriKind.Absolute, out var parsed) ? parsed : null;
        }
    }

    /// <summary>
    /// Presents the web view as Safari.
    /// </summary>
    /// <remarks>
    /// GOG is more inclined to serve a CAPTCHA to a user agent it does not recognise, and a CAPTCHA
    /// is the one thing an embedded browser handles badly.
    /// </remarks>
    public void UseSafariUserAgent()
    {
        if (_webView == IntPtr.Zero)
        {
            return;
        }

        const string safari =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Version/17.4 Safari/605.1.15";

        Send(_webView, Selector("setCustomUserAgent:"), NsString(safari));
    }

    private static IntPtr NsString(string value) =>
        SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), value);

    public void Navigate(Uri url)
    {
        if (_webView == IntPtr.Zero)
        {
            // Asked for before the native control exists; load it as soon as it does.
            _pending = url;
            return;
        }

        var nsUrl = Send(GetClass("NSURL"), Selector("URLWithString:"), NsString(url.ToString()));
        var request = Send(GetClass("NSURLRequest"), Selector("requestWithURL:"), nsUrl);

        Send(_webView, Selector("loadRequest:"), request);
    }

    /// <summary>
    /// Keeps the native view's frame in step with the control. Avalonia does not size the child
    /// reliably here, and a WKWebView laid out against the wrong rectangle renders the page in the
    /// wrong place and breaks hit-testing with it.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
        {
            SyncFrame();
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!IsSupported)
        {
            return base.CreateNativeControlCore(parent);
        }

        LoadFramework(WebKitFramework);

        // A non-persistent data store keeps GOG's session cookies in memory for this view alone.
        // With WebKit's default persistent store they outlive sign-out, and GOG then recognises
        // the old session and redirects straight back into the previous account, so switching
        // accounts becomes impossible. The configuration is copied at init, so it has to be set
        // up front rather than on the finished view.
        var configuration = Send(Send(GetClass("WKWebViewConfiguration"), Selector("alloc")), Selector("init"));
        Send(configuration, Selector("setWebsiteDataStore:"),
            Send(GetClass("WKWebsiteDataStore"), Selector("nonPersistentDataStore")));

        _webView = SendRectAndObject(
            Send(GetClass("WKWebView"), Selector("alloc")),
            Selector("initWithFrame:configuration:"),
            default,
            configuration);

        // The view holds its own copy of the configuration.
        Send(configuration, Selector("release"));

        if (_webView == IntPtr.Zero)
        {
            // WebKit refused to give us a view; the caller falls back to the paste flow.
            return base.CreateNativeControlCore(parent);
        }

        Send(_webView, Selector("setAutoresizingMask:"), Resizable);

        if (_pending is { } pending)
        {
            _pending = null;
            Navigate(pending);
        }

        // The container does not exist until Avalonia has taken the handle back.
        Dispatcher.UIThread.Post(SyncFrame, DispatcherPriority.Loaded);

        return new PlatformHandle(_webView, "NSView");
    }

    /// <summary>
    /// Positions the native view, and the container Avalonia wrapped it in.
    /// </summary>
    /// <remarks>
    /// Avalonia leaves that container at a zero frame, and AppKit views do not clip to their
    /// superview, so the page ends up drawn against an empty box at the window's origin. The
    /// container is therefore placed here, converted from Avalonia's top-left coordinates into
    /// AppKit's bottom-left ones against the window's content view.
    /// </remarks>
    private void SyncFrame()
    {
        if (_webView == IntPtr.Zero || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var window = Send(_webView, Selector("window"));
        if (window == IntPtr.Zero)
        {
            return;
        }

        var content = SendGetRect(Send(window, Selector("contentView")), Selector("frame"));
        var offset = this.TranslatePoint(default, topLevel) ?? default;
        var flipped = content.Height - offset.Y - Bounds.Height;

        if (Send(_webView, Selector("superview")) is var superview && superview != IntPtr.Zero)
        {
            SendRect(
                superview,
                Selector("setFrame:"),
                new CGRect(offset.X, flipped, Bounds.Width, Bounds.Height));
        }

        SendRect(_webView, Selector("setFrame:"), new CGRect(0, 0, Bounds.Width, Bounds.Height));
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_webView != IntPtr.Zero)
        {
            // Detached but deliberately not released: Avalonia retains the view it was handed, and
            // guessing wrong about ownership costs a crash, where guessing wrong the other way
            // costs one leaked web view per sign-in.
            Send(_webView, Selector("removeFromSuperview"));
            _webView = IntPtr.Zero;
        }
        else
        {
            base.DestroyNativeControlCore(control);
        }
    }
}

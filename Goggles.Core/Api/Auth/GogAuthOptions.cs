namespace Goggles.Core.Api.Auth;

/// <summary>
/// OAuth2 parameters for the GOG Galaxy client. These credentials are Galaxy's own and are
/// published in the community API documentation; every third-party GOG client uses them, as
/// GOG issues no client registrations of its own.
/// </summary>
public sealed record GogAuthOptions
{
    public string ClientId { get; init; } = "46899977096215655";

    public string ClientSecret { get; init; } =
        "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";

    /// <summary>
    /// Where GOG sends the browser after a successful login. This is a real web page rather than
    /// a loopback address, so the host application has to observe the navigation itself and hand
    /// the code back via <see cref="AuthService.TryExtractCode"/>.
    /// </summary>
    public Uri RedirectUri { get; init; } = new("https://embed.gog.com/on_login_success?origin=client");

    /// <summary>How long before real expiry a token is treated as expired.</summary>
    public TimeSpan ExpirySkew { get; init; } = TimeSpan.FromMinutes(1);
}

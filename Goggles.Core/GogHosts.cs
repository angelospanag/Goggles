namespace Goggles.Core;

/// <summary>
/// Base addresses for the GOG hosts Goggles talks to. Each one is a separate API with its own
/// conventions, so clients are kept per-host rather than merged behind a single facade.
/// </summary>
public static class GogHosts
{
    /// <summary>OAuth2 authorization and token endpoints.</summary>
    public static readonly Uri Auth = new("https://auth.gog.com/");

    /// <summary>Legacy account/library endpoints (owned games, wishlist, tags).</summary>
    public static readonly Uri Embed = new("https://embed.gog.com/");

    /// <summary>Product metadata and secure download links.</summary>
    public static readonly Uri Api = new("https://api.gog.com/");

    /// <summary>Per-product build lists.</summary>
    public static readonly Uri ContentSystem = new("https://content-system.gog.com/");

    /// <summary>Manifests and depot chunks.</summary>
    public static readonly Uri Cdn = new("https://cdn.gog.com/");

    /// <summary>Achievements and play sessions. Undocumented — reconstructed from community
    /// GOG-client reimplementations (gogapidocs, Heroic Games Launcher).</summary>
    public static readonly Uri Gameplay = new("https://gameplay.gog.com/");
}

namespace Goggles.Core.Api.Embed.Models;

/// <summary>
/// Response of <c>embed.gog.com/account/gameDetails/{id}.json</c>.
/// </summary>
/// <remarks>
/// The offline-installer <c>downloads</c> field is deliberately not modelled: it uses a
/// heterogeneous <c>[["English", {…}]]</c> array shape that needs its own converter, and Goggles
/// installs through the content-system depots rather than offline installers. <c>extras</c>, by
/// contrast, is a plain array and is modelled as <see cref="GameExtra"/>.
/// </remarks>
public sealed record GameDetails
{
    public string? Title { get; init; }

    public string? BackgroundImage { get; init; }

    public string? CdKey { get; init; }

    public string? TextInformation { get; init; }

    public string? Changelog { get; init; }

    public string? ForumLink { get; init; }

    public bool IsPreOrder { get; init; }

    public bool IsBaseProductMissing { get; init; }

    public long ReleaseTimestamp { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Goodies: manuals, soundtracks, wallpapers, add-ons and the like.</summary>
    public IReadOnlyList<GameExtra> Extras { get; init; } = [];

    /// <summary>Only the DLC the account owns. DLC entries carry the same shape as their parent.</summary>
    public IReadOnlyList<GameDetails> Dlcs { get; init; } = [];
}

/// <summary>One goodie from <see cref="GameDetails.Extras"/>.</summary>
/// <remarks>
/// Captured from live responses: <c>{manualUrl: "/downloads/{slug}/{id}", name, type, info, size}</c>.
/// <c>type</c> is GOG's own label (<c>"manuals"</c>, <c>"audio"</c>, <c>"game add-ons"</c>…) and is
/// not always right — one "avatars" entry was a 3 GB installer. <c>size</c> is display text such as
/// <c>"5 MB"</c>, not a number.
/// </remarks>
public sealed record GameExtra
{
    /// <summary>Relative to embed.gog.com; see <see cref="EmbedClient.DownloadExtraAsync"/>.</summary>
    public string? ManualUrl { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    /// <summary>How many files the goodie bundles, when GOG says.</summary>
    public int Info { get; init; }

    public string? Size { get; init; }
}

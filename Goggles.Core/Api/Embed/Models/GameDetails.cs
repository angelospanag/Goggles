namespace Goggles.Core.Api.Embed.Models;

/// <summary>
/// Response of <c>embed.gog.com/account/gameDetails/{id}.json</c>.
/// </summary>
/// <remarks>
/// The offline-installer <c>downloads</c> and <c>extras</c> fields are deliberately not modelled:
/// they use a heterogeneous <c>[["English", {…}]]</c> array shape that needs its own converter, and
/// Goggles installs through the content-system depots rather than offline installers. Add them if
/// and when offline installers become a feature.
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

    /// <summary>DLC entries carry the same shape as their parent.</summary>
    public IReadOnlyList<GameDetails> Dlcs { get; init; } = [];
}

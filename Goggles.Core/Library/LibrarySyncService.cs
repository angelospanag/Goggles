using Goggles.Core.Api.Embed;
using Goggles.Core.Api.Embed.Models;

namespace Goggles.Core.Library;

/// <summary>
/// Pulls the user's owned library from GOG and maps it into <see cref="OwnedGame"/> records.
/// </summary>
/// <remarks>
/// Persistence is deliberately not handled here — this returns what GOG currently reports, and the
/// caller decides what to do with it. The local SQLite cache slots in above this service.
/// </remarks>
public sealed class LibrarySyncService
{
    private readonly EmbedClient _embed;

    public LibrarySyncService(EmbedClient embed) => _embed = embed;

    /// <summary>
    /// Fetches the library, unless <paramref name="knownChecksum"/> matches the one GOG reports —
    /// a single cheap request that avoids paging through an unchanged library on every launch.
    /// </summary>
    /// <param name="knownChecksum">
    /// The <see cref="LibrarySyncResult.Checksum"/> from a previous sync, or null to force a fetch.
    /// </param>
    /// <param name="includeHidden">Also fetch the products the user has hidden.</param>
    public async Task<LibrarySyncResult> SyncAsync(
        string? knownChecksum = null,
        bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        var userData = await _embed.GetUserDataAsync(cancellationToken).ConfigureAwait(false);
        var checksum = userData.Checksum?.Games;

        if (knownChecksum is not null && checksum == knownChecksum)
        {
            return LibrarySyncResult.Unchanged(checksum, userData.Username);
        }

        var games = new List<OwnedGame>();
        var visible = await FetchAllPagesAsync(games, hidden: false, cancellationToken).ConfigureAwait(false);
        var pages = visible.Pages;

        // The visible pass reports whether anything is hidden at all, so most accounts never pay
        // for the second walk.
        if (includeHidden && visible.HasHiddenProducts)
        {
            var hidden = await FetchAllPagesAsync(games, hidden: true, cancellationToken).ConfigureAwait(false);
            pages += hidden.Pages;
        }

        return new LibrarySyncResult
        {
            Changed = true,
            Checksum = checksum,
            AccountName = userData.Username,
            Games = games,
            PagesFetched = pages,
        };
    }

    private static OwnedGame Map(FilteredProduct product) => new()
    {
        Id = product.Id,
        Title = product.Title ?? string.Empty,
        Slug = product.Slug,
        ImageBaseUrl = NormaliseImageUrl(product.Image),
        Category = product.Category,
        ReleasedAt = product.ReleaseDate?.ToDateTimeOffset(),
        Platforms = MapPlatforms(product.WorksOn),
        DlcCount = product.DlcCount,
        IsHidden = product.IsHidden,
        IsGalaxyCompatible = product.IsGalaxyCompatible,
        IsInDevelopment = product.IsInDevelopment,
        TagIds = product.Tags,
        CommunityRating = product.Rating,
    };

    private async Task<(int Pages, bool HasHiddenProducts)> FetchAllPagesAsync(
        List<OwnedGame> games,
        bool hidden,
        CancellationToken cancellationToken)
    {
        var first = await _embed
            .GetFilteredProductsAsync(page: 1, hidden: hidden, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        games.AddRange(first.Products.Select(Map));

        for (var page = 2; page <= first.TotalPages; page++)
        {
            var next = await _embed
                .GetFilteredProductsAsync(page, hidden, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // GOG has been known to report more pages than it will actually serve; stopping on an
            // empty page keeps a bad totalPages from turning into a long run of pointless requests.
            if (next.Products.Count == 0)
            {
                return (page, first.HasHiddenProducts);
            }

            games.AddRange(next.Products.Select(Map));
        }

        return (Math.Max(first.TotalPages, 1), first.HasHiddenProducts);
    }

    private static GamePlatforms MapPlatforms(WorksOn? worksOn)
    {
        if (worksOn is null)
        {
            return GamePlatforms.None;
        }

        var platforms = GamePlatforms.None;

        if (worksOn.Windows)
        {
            platforms |= GamePlatforms.Windows;
        }

        if (worksOn.Mac)
        {
            platforms |= GamePlatforms.Mac;
        }

        if (worksOn.Linux)
        {
            platforms |= GamePlatforms.Linux;
        }

        return platforms;
    }

    /// <summary>GOG sends protocol-relative image URLs; make them usable as-is.</summary>
    private static Uri? NormaliseImageUrl(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var absolute = image.StartsWith("//", StringComparison.Ordinal) ? $"https:{image}" : image;

        return Uri.TryCreate(absolute, UriKind.Absolute, out var uri) ? uri : null;
    }
}

using System.Text.Json.Serialization;
using Goggles.Core.Api.Embed.Serialization;

namespace Goggles.Core.Api.Embed.Models;

/// <summary>One page of <c>embed.gog.com/account/getFilteredProducts</c>.</summary>
public sealed record FilteredProductsPage
{
    public int Page { get; init; }

    public int TotalPages { get; init; }

    public int TotalProducts { get; init; }

    public int ProductsPerPage { get; init; }

    public int MoviesCount { get; init; }

    public string? SortBy { get; init; }

    /// <summary>Whether the account has any hidden products at all — worth checking before asking for them.</summary>
    public bool HasHiddenProducts { get; init; }

    public int UpdatedProductsCount { get; init; }

    public int HiddenUpdatedProductsCount { get; init; }

    public IReadOnlyList<FilteredProduct> Products { get; init; } = [];

    /// <summary>The user's tag definitions; products reference these by id.</summary>
    public IReadOnlyList<LibraryTag> Tags { get; init; } = [];
}

public sealed record FilteredProduct
{
    public long Id { get; init; }

    public string? Title { get; init; }

    public string? Slug { get; init; }

    public string? Url { get; init; }

    /// <summary>Protocol-relative and without a file extension, e.g. <c>//images.gog.com/abc</c>.</summary>
    public string? Image { get; init; }

    public string? Category { get; init; }

    /// <summary>
    /// GOG's community average rating in whole stars, 0–5; 0 when the product has none. Not the
    /// user's own rating — that lives in <c>user/games_rating.json</c>, on a stars×10 scale.
    /// Confirmed live: older samples show tenths (e.g. 41), but GOG now sends whole stars.
    /// </summary>
    public int Rating { get; init; }

    public int DlcCount { get; init; }

    public int Updates { get; init; }

    public bool IsNew { get; init; }

    public bool IsHidden { get; init; }

    public bool IsGame { get; init; }

    public bool IsMovie { get; init; }

    public bool IsGalaxyCompatible { get; init; }

    public bool IsInDevelopment { get; init; }

    public bool IsComingSoon { get; init; }

    public WorksOn? WorksOn { get; init; }

    public PhpDateTime? ReleaseDate { get; init; }

    public ProductAvailability? Availability { get; init; }

    /// <summary>Tag ids, which GOG has been observed to send both as bare strings and as objects.</summary>
    [JsonConverter(typeof(TagIdListConverter))]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record WorksOn
{
    public bool Windows { get; init; }

    public bool Mac { get; init; }

    public bool Linux { get; init; }
}

public sealed record ProductAvailability
{
    public bool IsAvailable { get; init; }

    public bool IsAvailableInAccount { get; init; }
}

public sealed record LibraryTag
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>How many products carry this tag.</summary>
    public int ProductCount { get; init; }
}

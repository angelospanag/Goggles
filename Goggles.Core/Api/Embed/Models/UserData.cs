using System.Text.Json.Serialization;

namespace Goggles.Core.Api.Embed.Models;

/// <summary>Response of <c>embed.gog.com/userData.json</c>.</summary>
public sealed record UserData
{
    public string? UserId { get; init; }

    public string? GalaxyUserId { get; init; }

    public string? Username { get; init; }

    public string? Email { get; init; }

    public string? Avatar { get; init; }

    public string? Country { get; init; }

    public bool IsLoggedIn { get; init; }

    public UserDataChecksum? Checksum { get; init; }

    public PurchasedItems? PurchasedItems { get; init; }
}

/// <summary>
/// Opaque per-collection revision markers. They change whenever the collection does, which makes
/// them a cheap way to skip a full library fetch.
/// </summary>
/// <remarks>
/// Most of embed's JSON is camelCase, but these two keys are snake_case — confirmed against a live
/// response, hence the explicit names.
/// </remarks>
public sealed record UserDataChecksum
{
    public string? Games { get; init; }

    public string? Wishlist { get; init; }

    public string? Cart { get; init; }

    [JsonPropertyName("reviews_votes")]
    public string? ReviewsVotes { get; init; }

    [JsonPropertyName("games_rating")]
    public string? GamesRating { get; init; }
}

public sealed record PurchasedItems
{
    public int Games { get; init; }

    public int Movies { get; init; }
}

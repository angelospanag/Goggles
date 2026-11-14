namespace Goggles.Core.Api.Auth;

/// <summary>
/// A stored set of GOG credentials. Unlike the wire response this holds an absolute expiry so it
/// stays meaningful after being persisted and reloaded.
/// </summary>
public sealed record TokenSet
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public string? UserId { get; init; }

    public string? SessionId { get; init; }

    public bool IsExpired(DateTimeOffset now, TimeSpan skew) => now + skew >= ExpiresAt;
}

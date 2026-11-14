namespace Goggles.Core.Api.Auth;

/// <summary>Raw shape of the <c>auth.gog.com/token</c> response.</summary>
internal sealed record TokenResponse
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public int ExpiresIn { get; init; }

    public string? UserId { get; init; }

    public string? SessionId { get; init; }

    public string? TokenType { get; init; }
}

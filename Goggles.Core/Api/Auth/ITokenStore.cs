namespace Goggles.Core.Api.Auth;

/// <summary>
/// Persistence for the signed-in user's credentials. Implementations are expected to be safe for
/// concurrent use.
/// </summary>
public interface ITokenStore
{
    ValueTask<TokenSet?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

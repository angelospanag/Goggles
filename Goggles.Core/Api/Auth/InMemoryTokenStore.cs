namespace Goggles.Core.Api.Auth;

/// <summary>Non-persistent token store, for tests and short-lived tooling.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly Lock _gate = new();
    private TokenSet? _tokens;

    public InMemoryTokenStore(TokenSet? initial = null) => _tokens = initial;

    public ValueTask<TokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_tokens);
        }
    }

    public ValueTask SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _tokens = tokens;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _tokens = null;
        }

        return ValueTask.CompletedTask;
    }
}

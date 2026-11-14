using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Goggles.Core.Api.Auth;

/// <summary>
/// Drives the GOG OAuth2 flow and keeps a usable access token available.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> handed to this service must not itself be wired through
/// <see cref="GogAuthHandler"/> — the token endpoint is unauthenticated, and doing so would
/// recurse.
/// </remarks>
public sealed class AuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly ITokenStore _store;
    private readonly GogAuthOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public AuthService(
        HttpClient http,
        ITokenStore store,
        GogAuthOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _store = store;
        _options = options ?? new GogAuthOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when GOG no longer accepts the stored credentials, or there are none, so only signing
    /// in again can help. Fires on whichever thread noticed, and can fire several times for one
    /// expiry — every in-flight request finds out on its own — so handlers must tolerate both.
    /// </summary>
    public event EventHandler? SignInRequired;

    /// <summary>
    /// Raises <see cref="SignInRequired"/> for a rejection noticed outside this class — an API
    /// answering with GOG's login page even after a refresh, say.
    /// </summary>
    public void ReportSignInRequired() => SignInRequired?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The page to open in a browser to start sign-in. The caller is responsible for watching the
    /// browser's navigation and passing any redirect back to <see cref="TryExtractCode"/>.
    /// </summary>
    public Uri BuildAuthorizationUrl() => BuildUri("auth", new Dictionary<string, string>
    {
        ["client_id"] = _options.ClientId,
        ["redirect_uri"] = _options.RedirectUri.ToString(),
        ["response_type"] = "code",
        ["layout"] = "client2",
    });

    /// <summary>
    /// Recognises the post-login redirect and pulls the authorization code out of it. Returns
    /// false for every other navigation, so it can be called on each redirect the browser reports.
    /// </summary>
    public bool TryExtractCode(Uri navigatedTo, out string? code)
    {
        code = null;

        var sameEndpoint = Uri.Compare(
            navigatedTo,
            _options.RedirectUri,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;

        if (!sameEndpoint)
        {
            return false;
        }

        code = ParseQuery(navigatedTo.Query).GetValueOrDefault("code");
        return !string.IsNullOrEmpty(code);
    }

    /// <summary>Trades an authorization code for tokens and stores them.</summary>
    public async Task<TokenSet> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var tokens = await RequestTokensAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri.ToString(),
        }, cancellationToken).ConfigureAwait(false);

        await _store.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
        return tokens;
    }

    /// <summary>
    /// Returns a token that is valid now, refreshing first if the stored one has expired.
    /// </summary>
    /// <exception cref="GogAuthException">No credentials are stored, or the refresh was rejected.</exception>
    public async Task<TokenSet> GetValidTokenAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await SignalRejectionAsync(() => LoadOrThrowAsync(cancellationToken)).ConfigureAwait(false);

        return tokens.IsExpired(_time.GetUtcNow(), _options.ExpirySkew)
            ? await RefreshAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false)
            : tokens;
    }

    /// <summary>
    /// Refreshes the stored credentials, unless another caller already replaced
    /// <paramref name="staleAccessToken"/> with a usable one while this call was waiting.
    /// </summary>
    public Task<TokenSet> RefreshAsync(string staleAccessToken, CancellationToken cancellationToken = default) =>
        SignalRejectionAsync(() => RefreshCoreAsync(staleAccessToken, cancellationToken));

    private async Task<TokenSet> RefreshCoreAsync(string staleAccessToken, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadOrThrowAsync(cancellationToken).ConfigureAwait(false);
            if (current.AccessToken != staleAccessToken &&
                !current.IsExpired(_time.GetUtcNow(), _options.ExpirySkew))
            {
                return current;
            }

            var refreshed = await RequestTokensAsync(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = current.RefreshToken,
            }, cancellationToken).ConfigureAwait(false);

            await _store.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Trades the stored refresh token for credentials issued to another OAuth client — a game's own
    /// Galaxy <c>clientId</c>/<c>clientSecret</c>, which is what its SDK expects to be handed. This is
    /// what Galaxy's local service does (observed by Comet, not documented by GOG).
    /// <c>without_new_session</c> leaves the user's own session, and so the stored tokens, untouched;
    /// the result is not stored.
    /// </summary>
    /// <remarks>
    /// A refusal never raises <see cref="SignInRequired"/>: a 403 here means the account does not own
    /// the game, which says nothing about the session itself.
    /// </remarks>
    /// <exception cref="GogAuthException">No credentials are stored, or GOG refused the exchange.</exception>
    public async Task<TokenSet> GetTokenForClientAsync(
        string clientId,
        string clientSecret,
        bool openId = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var current = await SignalRejectionAsync(() => LoadOrThrowAsync(cancellationToken)).ConfigureAwait(false);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["without_new_session"] = "1",
        };

        if (openId)
        {
            query["scope"] = "openid";
        }

        try
        {
            return await RequestTokensAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (GogAuthException exception) when (exception.RequiresSignIn)
        {
            throw new GogAuthException(
                $"GOG refused a token for client {clientId}: {exception.Message}",
                exception.StatusCode,
                exception.ResponseBody);
        }
    }

    /// <summary>Forgets the stored credentials.</summary>
    public ValueTask SignOutAsync(CancellationToken cancellationToken = default) =>
        _store.ClearAsync(cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> and raises <see cref="SignInRequired"/> if it fails in a way
    /// only signing in again can fix. Wraps loading and refreshing only: a rejected code during
    /// sign-in itself is reported to the sign-in screen, which is already where the user is.
    /// </summary>
    private async Task<TokenSet> SignalRejectionAsync(Func<Task<TokenSet>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (GogAuthException exception) when (exception.RequiresSignIn)
        {
            ReportSignInRequired();
            throw;
        }
    }

    private async Task<TokenSet> LoadOrThrowAsync(CancellationToken cancellationToken)
    {
        return await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
               ?? throw new GogAuthException("No GOG credentials are stored; sign in first.")
               {
                   RequiresSignIn = true,
               };
    }

    private async Task<TokenSet> RequestTokensAsync(
        Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(BuildUri("token", query), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GogAuthException(
                $"GOG rejected the token request ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode,
                body)
            {
                // A revoked or expired refresh token comes back as one of these. Other 4xx codes —
                // 429 rate limiting, 408 timeouts — and every 5xx say nothing about the credentials,
                // and must not throw a working session away.
                RequiresSignIn = response.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden,
            };
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null ||
            string.IsNullOrEmpty(payload.AccessToken) ||
            string.IsNullOrEmpty(payload.RefreshToken))
        {
            throw new GogAuthException("GOG returned a token response with no usable tokens.");
        }

        return new TokenSet
        {
            AccessToken = payload.AccessToken,
            RefreshToken = payload.RefreshToken,
            ExpiresAt = _time.GetUtcNow().AddSeconds(payload.ExpiresIn),
            UserId = payload.UserId,
            SessionId = payload.SessionId,
        };
    }

    private static Uri BuildUri(string path, Dictionary<string, string> query)
    {
        var encoded = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new Uri(GogHosts.Auth, $"{path}?{encoded}");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            values[Uri.UnescapeDataString(pair[..separator])] =
                Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return values;
    }
}

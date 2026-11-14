using System.Net;
using System.Net.Http.Headers;

namespace Goggles.Core.Api.Auth;

/// <summary>
/// Attaches the bearer token to outgoing requests and retries once against a freshly refreshed
/// token if GOG rejects the request. Wire this into every authenticated client — but never into the
/// <see cref="HttpClient"/> that <see cref="AuthService"/> itself uses.
/// </summary>
/// <remarks>
/// A rejection is a 401, or — from embed.gog.com — GOG's HTML login page served with a 200. If the
/// login page comes back even after a refresh, the session is dead and
/// <see cref="AuthService.SignInRequired"/> is raised. A 401 that survives the refresh is not
/// treated that way: individual endpoints can refuse individual products, and signing someone out
/// over one game's achievements would be far worse than an error on that game.
/// </remarks>
public sealed class GogAuthHandler : DelegatingHandler
{
    private readonly AuthService _auth;

    public GogAuthHandler(AuthService auth) => _auth = auth;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Buffered up front: the original request cannot be sent twice, so a retry needs a copy,
        // and a copy needs content that can be read again.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var tokens = await _auth.GetValidTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsRejected(request, response))
        {
            return response;
        }

        response.Dispose();

        var refreshed = await _auth.RefreshAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        using var retry = Clone(request, body);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        var retried = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        if (IsLoginPage(retry, retried))
        {
            _auth.ReportSignInRequired();
        }

        return retried;
    }

    private static bool IsRejected(HttpRequestMessage request, HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized || IsLoginPage(request, response);

    /// <summary>
    /// embed.gog.com answers a signed-out session with its HTML login page and a 200 rather than a
    /// 401 — every endpoint there that this client calls returns JSON otherwise.
    /// </summary>
    private static bool IsLoginPage(HttpRequestMessage request, HttpResponseMessage response) =>
        response.IsSuccessStatusCode &&
        string.Equals(request.RequestUri?.Host, GogHosts.Embed.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in (IDictionary<string, object?>)request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return clone;
    }
}

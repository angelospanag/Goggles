using System.Net;

namespace Goggles.Core.Api.Auth;

/// <summary>Raised when GOG rejects a sign-in, or when no usable credentials are stored.</summary>
public sealed class GogAuthException : Exception
{
    public GogAuthException(string message, HttpStatusCode? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }

    /// <summary>
    /// True when only signing in again can fix this: nothing is stored, or the token endpoint
    /// refused the credentials (400, 401 or 403). False for rate limiting and server-side failures,
    /// which are worth retrying rather than throwing the session away over.
    /// </summary>
    public bool RequiresSignIn { get; init; }
}

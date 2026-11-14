using System.Net;

namespace Goggles.Core.Api;

/// <summary>Raised when a GOG endpoint answers with something other than success.</summary>
public sealed class GogApiException : Exception
{
    public GogApiException(string message, HttpStatusCode? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }
}

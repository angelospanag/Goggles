using System.Net;
using System.Text;

namespace Goggles.Core.Tests;

/// <summary>Records the requests it is handed and replies with whatever the test dictates.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private readonly Lock _gate = new();
    private int _callCount;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) =>
        _responder = responder;

    public StubHttpMessageHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        : this((_, _) => Json(json, status))
    {
    }

    public List<Uri> RequestedUris { get; } = [];

    public List<string?> AuthorizationHeaders { get; } = [];

    public int CallCount => Volatile.Read(ref _callCount);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Bytes(byte[] payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new ByteArrayContent(payload),
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int ordinal;
        lock (_gate)
        {
            RequestedUris.Add(request.RequestUri!);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            ordinal = ++_callCount;
        }

        return Task.FromResult(_responder(request, ordinal));
    }
}

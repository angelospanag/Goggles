using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Goggles.Core.Diagnostics;

/// <summary>
/// Writes every response body to disk so real GOG payloads can be turned into test fixtures.
/// </summary>
/// <remarks>
/// Credentials are redacted on the way out. The token endpoint answers with an access token and a
/// refresh token, and is *asked* with the client secret and authorization code in the query string,
/// so both the body and the URL are scrubbed before anything is written or logged.
/// <para>
/// Buffers the whole body in memory, so this is for exploring JSON endpoints only — never put it in
/// front of depot chunk downloads.
/// </para>
/// </remarks>
public sealed class ResponseDumpHandler : DelegatingHandler
{
    /// <summary>JSON properties whose values are credentials wherever they appear.</summary>
    private static readonly HashSet<string> SensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "refresh_token",
        "session_id",
        "user_id",
        "client_secret",
    };

    /// <summary>
    /// Query parameters worth hiding. <c>code</c> is included here but deliberately not in
    /// <see cref="SensitiveFields"/>, since plenty of unrelated payloads have a harmless
    /// <c>code</c> property and redacting those would spoil the fixtures.
    /// </summary>
    private static readonly HashSet<string> SensitiveParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "code",
        "client_secret",
        "access_token",
        "refresh_token",
        "token",
        "wsSecret",
    };

    private const string Redacted = "<redacted>";

    private readonly string _directory;
    private readonly TextWriter? _log;
    private int _sequence;

    public ResponseDumpHandler(string directory, TextWriter? log = null)
    {
        _directory = directory;
        _log = log;

        Directory.CreateDirectory(directory);
    }

    /// <summary>Hides credential values in a URL, for logging and for building file names.</summary>
    public static string DescribeUri(Uri? uri)
    {
        if (uri is null)
        {
            return "unknown";
        }

        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri.ToString();
        }

        var parameters = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var separator = pair.IndexOf('=');
                if (separator < 0)
                {
                    return pair;
                }

                var key = Uri.UnescapeDataString(pair[..separator]);

                return SensitiveParameters.Contains(key) ? $"{pair[..separator]}={Redacted}" : pair;
            });

        return $"{uri.GetLeftPart(UriPartial.Path)}?{string.Join('&', parameters)}";
    }

    /// <summary>
    /// Replaces credential values in a JSON body. Bodies that are not JSON are returned unchanged,
    /// so callers must not send this anything sensitive that is not JSON.
    /// </summary>
    public static string RedactBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        try
        {
            if (JsonNode.Parse(body) is not { } node)
            {
                return body;
            }

            Redact(node);

            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // HTML error pages and the like: nothing to walk, and nothing token-shaped in them.
            return body;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // ReadAsStringAsync buffers the content, so the caller can still read it afterwards.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var described = DescribeUri(request.RequestUri);
        var path = Path.Combine(_directory, FileNameFor(described));

        await File.WriteAllTextAsync(path, RedactBody(body), cancellationToken).ConfigureAwait(false);
        _log?.WriteLine($"  dumped {(int)response.StatusCode} {described} -> {path}");

        return response;
    }

    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (SensitiveFields.Contains(key))
                    {
                        o[key] = Redacted;
                    }
                    else if (value is not null)
                    {
                        Redact(value);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array.Where(item => item is not null))
                {
                    Redact(item!);
                }

                break;
        }
    }

    private string FileNameFor(string describedUri)
    {
        var ordinal = Interlocked.Increment(ref _sequence);
        var name = new StringBuilder($"{ordinal:D2}-");

        foreach (var character in describedUri.Replace("https://", string.Empty, StringComparison.Ordinal))
        {
            name.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '_');
        }

        return $"{name.ToString()[..Math.Min(name.Length, 120)]}.json";
    }
}

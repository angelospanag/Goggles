using Goggles.Core.Diagnostics;

namespace Goggles.Core.Tests.Diagnostics;

public class ResponseDumpHandlerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-dump").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Shape of a real auth.gog.com/token response.</summary>
    private const string TokenResponse = """
        {
          "access_token": "the-real-access-token",
          "refresh_token": "the-real-refresh-token",
          "expires_in": 3600,
          "token_type": "bearer",
          "user_id": "48485266454929432",
          "session_id": "the-real-session"
        }
        """;

    [Fact]
    public void Redacts_every_credential_in_a_token_response()
    {
        var redacted = ResponseDumpHandler.RedactBody(TokenResponse);

        Assert.DoesNotContain("the-real-access-token", redacted);
        Assert.DoesNotContain("the-real-refresh-token", redacted);
        Assert.DoesNotContain("the-real-session", redacted);
        Assert.DoesNotContain("48485266454929432", redacted);

        // The shape survives, so the dump is still useful as a fixture.
        Assert.Contains("\"expires_in\": 3600", redacted);
        Assert.Contains("bearer", redacted);
    }

    [Fact]
    public void Redacts_credentials_nested_anywhere_in_the_body()
    {
        var redacted = ResponseDumpHandler.RedactBody(
            """{"depots":[{"clientSecret":"x","client_secret":"leaked"}],"a":{"b":{"access_token":"leaked"}}}""");

        Assert.DoesNotContain("leaked", redacted);
    }

    [Fact]
    public void Leaves_ordinary_payloads_alone()
    {
        var library = """{"username":"someone","checksum":{"games":"abc123"},"code":"en-US"}""";

        var redacted = ResponseDumpHandler.RedactBody(library);

        Assert.Contains("someone", redacted);
        Assert.Contains("abc123", redacted);

        // "code" is a query-string concern; plenty of bodies use it innocently.
        Assert.Contains("en-US", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<!DOCTYPE html><html><body>Sign in</body></html>")]
    public void Passes_through_anything_that_is_not_json(string body)
    {
        Assert.Equal(body, ResponseDumpHandler.RedactBody(body));
    }

    [Fact]
    public void Hides_the_client_secret_and_code_in_a_token_request_url()
    {
        var uri = new Uri(
            "https://auth.gog.com/token?client_id=46899977096215655&client_secret=the-real-secret" +
            "&grant_type=authorization_code&code=the-real-code&redirect_uri=https%3A%2F%2Fembed.gog.com%2F");

        var described = ResponseDumpHandler.DescribeUri(uri);

        Assert.DoesNotContain("the-real-secret", described);
        Assert.DoesNotContain("the-real-code", described);
        Assert.Contains("client_id=46899977096215655", described);
        Assert.Contains("grant_type=authorization_code", described);
    }

    [Fact]
    public void Hides_a_cdn_signing_token()
    {
        var uri = new Uri("https://gog-cdn-lb.gog.com/store/ab/cd/abcd?wsSecret=the-real-token&wsTime=1786579399");

        var described = ResponseDumpHandler.DescribeUri(uri);

        Assert.DoesNotContain("the-real-token", described);
        Assert.Contains("wsTime=1786579399", described);
    }

    [Fact]
    public void Leaves_urls_without_credentials_untouched()
    {
        var uri = new Uri("https://embed.gog.com/account/getFilteredProducts?mediaType=1&page=2");

        Assert.Equal(uri.ToString(), ResponseDumpHandler.DescribeUri(uri));
    }

    [Fact]
    public async Task Writes_a_redacted_file_and_keeps_the_body_readable_downstream()
    {
        var handler = new ResponseDumpHandler(_directory)
        {
            InnerHandler = new StubHttpMessageHandler(TokenResponse),
        };

        using var client = new HttpClient(handler);
        var response = await client.GetAsync(
            "https://auth.gog.com/token?client_secret=the-real-secret&code=the-real-code",
            TestContext.Current.CancellationToken);

        // The caller still sees the true response; only the dump is scrubbed.
        Assert.Contains("the-real-access-token", await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        var dump = Directory.EnumerateFiles(_directory).Single();
        var contents = await File.ReadAllTextAsync(dump, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("the-real-access-token", contents);
        Assert.DoesNotContain("the-real-secret", Path.GetFileName(dump));
        Assert.DoesNotContain("the-real-code", Path.GetFileName(dump));
    }
}

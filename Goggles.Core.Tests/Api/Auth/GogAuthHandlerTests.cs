using System.Net;
using Goggles.Core.Api.Auth;

namespace Goggles.Core.Tests.Api.Auth;

public class GogAuthHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    private const string TokenJson = """
        {
          "access_token": "fresh-access",
          "refresh_token": "fresh-refresh",
          "expires_in": 3600,
          "token_type": "bearer"
        }
        """;

    [Fact]
    public async Task Attaches_the_stored_bearer_token()
    {
        var inner = new StubHttpMessageHandler("""{"ok":true}""");
        var client = CreateClient(inner, new StubHttpMessageHandler(TokenJson), Live);

        using var response = await client.GetAsync(
            "https://api.gog.com/products/1207658930",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer stored-access", Assert.Single(inner.AuthorizationHeaders));
    }

    [Fact]
    public async Task Refreshes_and_retries_once_on_401()
    {
        var inner = new StubHttpMessageHandler((_, call) => call == 1
            ? StubHttpMessageHandler.Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)
            : StubHttpMessageHandler.Json("""{"ok":true}"""));

        var auth = new StubHttpMessageHandler(TokenJson);
        var client = CreateClient(inner, auth, Live);

        using var response = await client.GetAsync(
            "https://api.gog.com/products/1207658930",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, auth.CallCount);
        Assert.Equal(
            new[] { "Bearer stored-access", "Bearer fresh-access" },
            inner.AuthorizationHeaders);
    }

    [Fact]
    public async Task Replays_the_request_body_on_retry()
    {
        var bodies = new List<string>();
        var inner = new StubHttpMessageHandler((request, call) =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().Result);
            return call == 1
                ? StubHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized)
                : StubHttpMessageHandler.Json("""{"ok":true}""");
        });

        var client = CreateClient(inner, new StubHttpMessageHandler(TokenJson), Live);

        using var response = await client.PostAsync(
            "https://embed.gog.com/friends/search",
            new StringContent("""{"query":"someone"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, body => Assert.Equal("""{"query":"someone"}""", body));
    }

    [Fact]
    public async Task Does_not_retry_a_second_time()
    {
        var inner = new StubHttpMessageHandler("{}", HttpStatusCode.Unauthorized);
        var client = CreateClient(inner, new StubHttpMessageHandler(TokenJson), Live);

        using var response = await client.GetAsync(
            "https://api.gog.com/products/1207658930",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Refreshes_an_expired_token_before_the_first_attempt()
    {
        var inner = new StubHttpMessageHandler("""{"ok":true}""");
        var client = CreateClient(inner, new StubHttpMessageHandler(TokenJson), Now.AddMinutes(-1));

        using var response = await client.GetAsync(
            "https://api.gog.com/products/1207658930",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer fresh-access", Assert.Single(inner.AuthorizationHeaders));
    }

    [Fact]
    public async Task An_embed_login_page_is_retried_with_a_fresh_token()
    {
        var inner = new StubHttpMessageHandler((_, call) => call == 1 ? LoginPage() : StubHttpMessageHandler.Json("{}"));
        var (client, auth) = CreateClientWithAuth(inner, new StubHttpMessageHandler(TokenJson), Live);
        var signals = 0;
        auth.SignInRequired += (_, _) => signals++;

        using var response = await client.GetAsync(
            "https://embed.gog.com/userData.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("Bearer fresh-access", inner.AuthorizationHeaders[1]);
        Assert.Equal(0, signals);
    }

    [Fact]
    public async Task An_embed_login_page_that_survives_a_refresh_requires_signing_in_again()
    {
        var inner = new StubHttpMessageHandler((_, _) => LoginPage());
        var (client, auth) = CreateClientWithAuth(inner, new StubHttpMessageHandler(TokenJson), Live);
        var signals = 0;
        auth.SignInRequired += (_, _) => signals++;

        using var response = await client.GetAsync(
            "https://embed.gog.com/userData.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, signals);
    }

    [Fact]
    public async Task A_401_that_survives_a_refresh_does_not_sign_anyone_out()
    {
        var inner = new StubHttpMessageHandler("{}", HttpStatusCode.Unauthorized);
        var (client, auth) = CreateClientWithAuth(inner, new StubHttpMessageHandler(TokenJson), Live);
        var signals = 0;
        auth.SignInRequired += (_, _) => signals++;

        using var response = await client.GetAsync(
            "https://gameplay.gog.com/clients/1/users/2/achievements",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, signals);
    }

    [Fact]
    public async Task Html_from_a_host_other_than_embed_is_left_alone()
    {
        var inner = new StubHttpMessageHandler((_, _) => LoginPage());
        var (client, _) = CreateClientWithAuth(inner, new StubHttpMessageHandler(TokenJson), Live);

        using var response = await client.GetAsync(
            "https://www.gog.com/en/game/witcher_3",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.CallCount);
    }

    private static HttpResponseMessage LoginPage() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><body>Log in</body></html>", System.Text.Encoding.UTF8, "text/html"),
    };

    private static DateTimeOffset Live => Now.AddMinutes(30);

    private static HttpClient CreateClient(
        StubHttpMessageHandler inner,
        StubHttpMessageHandler authTransport,
        DateTimeOffset expiresAt) => CreateClientWithAuth(inner, authTransport, expiresAt).Client;

    private static (HttpClient Client, AuthService Auth) CreateClientWithAuth(
        StubHttpMessageHandler inner,
        StubHttpMessageHandler authTransport,
        DateTimeOffset expiresAt)
    {
        var store = new InMemoryTokenStore(new TokenSet
        {
            AccessToken = "stored-access",
            RefreshToken = "stored-refresh",
            ExpiresAt = expiresAt,
        });

        var auth = new AuthService(
            new HttpClient(authTransport),
            store,
            timeProvider: new FakeTimeProvider(Now));

        return (new HttpClient(new GogAuthHandler(auth) { InnerHandler = inner }), auth);
    }
}

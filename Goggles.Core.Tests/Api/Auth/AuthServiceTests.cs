using System.Net;
using Goggles.Core.Api.Auth;

namespace Goggles.Core.Tests.Api.Auth;

public class AuthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    private const string TokenJson = """
        {
          "access_token": "access-1",
          "refresh_token": "refresh-1",
          "expires_in": 3600,
          "token_type": "bearer",
          "user_id": "48485266454929432",
          "session_id": "session-1"
        }
        """;

    [Fact]
    public void BuildAuthorizationUrl_carries_the_galaxy_client_parameters()
    {
        var url = CreateService(new StubHttpMessageHandler(TokenJson)).Service.BuildAuthorizationUrl();

        Assert.Equal("https://auth.gog.com/auth", url.GetLeftPart(UriPartial.Path));
        Assert.Contains("client_id=46899977096215655", url.Query);
        Assert.Contains("response_type=code", url.Query);
        Assert.Contains("layout=client2", url.Query);
        Assert.Contains(
            Uri.EscapeDataString("https://embed.gog.com/on_login_success?origin=client"),
            url.Query);
    }

    [Fact]
    public void TryExtractCode_reads_the_code_from_the_success_redirect()
    {
        var service = CreateService(new StubHttpMessageHandler(TokenJson)).Service;
        var redirect = new Uri("https://embed.gog.com/on_login_success?origin=client&code=abc123");

        Assert.True(service.TryExtractCode(redirect, out var code));
        Assert.Equal("abc123", code);
    }

    [Theory]
    [InlineData("https://auth.gog.com/auth?client_id=46899977096215655")]
    [InlineData("https://embed.gog.com/on_login_success?origin=client")]
    [InlineData("https://embed.gog.com/something_else?code=abc123")]
    public void TryExtractCode_ignores_other_navigations(string url)
    {
        var service = CreateService(new StubHttpMessageHandler(TokenJson)).Service;

        Assert.False(service.TryExtractCode(new Uri(url), out var code));
        Assert.Null(code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_stores_tokens_with_an_absolute_expiry()
    {
        var handler = new StubHttpMessageHandler(TokenJson);
        var (service, store, _) = CreateService(handler);

        var tokens = await service.ExchangeCodeAsync(
            "the-code",
            TestContext.Current.CancellationToken);

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal("48485266454929432", tokens.UserId);
        Assert.Equal(Now.AddHours(1), tokens.ExpiresAt);
        Assert.Equal(tokens, await store.LoadAsync(TestContext.Current.CancellationToken));

        var query = handler.RequestedUris.Single().Query;
        Assert.Contains("grant_type=authorization_code", query);
        Assert.Contains("code=the-code", query);
        Assert.Contains("client_secret=", query);
    }

    [Fact]
    public async Task ExchangeCodeAsync_surfaces_a_rejection_with_the_body()
    {
        var handler = new StubHttpMessageHandler(
            """{"error":"invalid_grant"}""",
            HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<GogAuthException>(
            () => CreateService(handler).Service.ExchangeCodeAsync(
                "stale-code",
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid_grant", exception.ResponseBody);
        Assert.True(exception.RequiresSignIn);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_transient_failure_from_the_token_endpoint_does_not_require_signing_in_again(
        HttpStatusCode status)
    {
        var (service, _, _) = CreateService(
            new StubHttpMessageHandler("{}", status),
            StoredToken(Now.AddMinutes(-1)));
        var signals = 0;
        service.SignInRequired += (_, _) => signals++;

        var exception = await Assert.ThrowsAsync<GogAuthException>(
            () => service.GetValidTokenAsync(TestContext.Current.CancellationToken));

        Assert.False(exception.RequiresSignIn);
        Assert.Equal(0, signals);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_refresh_requires_signing_in_again(HttpStatusCode status)
    {
        var (service, _, _) = CreateService(
            new StubHttpMessageHandler("""{"error":"invalid_grant"}""", status),
            StoredToken(Now.AddMinutes(-1)));
        var signals = 0;
        service.SignInRequired += (_, _) => signals++;

        var exception = await Assert.ThrowsAsync<GogAuthException>(
            () => service.GetValidTokenAsync(TestContext.Current.CancellationToken));

        Assert.True(exception.RequiresSignIn);
        Assert.Equal(1, signals);
    }

    [Fact]
    public async Task A_rejected_sign_in_code_does_not_raise_SignInRequired()
    {
        var (service, _, _) = CreateService(
            new StubHttpMessageHandler("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        var signals = 0;
        service.SignInRequired += (_, _) => signals++;

        await Assert.ThrowsAsync<GogAuthException>(
            () => service.ExchangeCodeAsync("stale-code", TestContext.Current.CancellationToken));

        Assert.Equal(0, signals);
    }

    [Fact]
    public async Task GetValidTokenAsync_throws_when_nothing_is_stored()
    {
        var (service, _, _) = CreateService(new StubHttpMessageHandler(TokenJson));

        var signals = 0;
        service.SignInRequired += (_, _) => signals++;

        var exception = await Assert.ThrowsAsync<GogAuthException>(() => service.GetValidTokenAsync(
            TestContext.Current.CancellationToken));

        Assert.True(exception.RequiresSignIn);
        Assert.Equal(1, signals);
    }

    [Fact]
    public async Task GetValidTokenAsync_returns_a_live_token_without_calling_gog()
    {
        var handler = new StubHttpMessageHandler(TokenJson);
        var (service, _, _) = CreateService(handler, StoredToken(Now.AddMinutes(30)));

        var tokens = await service.GetValidTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("stored-access", tokens.AccessToken);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_refreshes_a_token_that_is_about_to_expire()
    {
        var handler = new StubHttpMessageHandler(TokenJson);
        var (service, store, _) = CreateService(handler, StoredToken(Now.AddSeconds(30)));

        var tokens = await service.GetValidTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal(tokens, await store.LoadAsync(TestContext.Current.CancellationToken));

        var query = handler.RequestedUris.Single().Query;
        Assert.Contains("grant_type=refresh_token", query);
        Assert.Contains("refresh_token=stored-refresh", query);
    }

    [Fact]
    public async Task Concurrent_callers_trigger_a_single_refresh()
    {
        var gate = new TaskCompletionSource();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            gate.Task.Wait();
            return StubHttpMessageHandler.Json(TokenJson);
        });

        var (service, _, _) = CreateService(handler, StoredToken(Now.AddMinutes(-5)));

        var callers = Enumerable
            .Range(0, 8)
            .Select(_ => Task.Run(() => service.GetValidTokenAsync()))
            .ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, tokens => Assert.Equal("access-1", tokens.AccessToken));
    }

    [Fact]
    public async Task RefreshAsync_keeps_the_token_another_caller_already_fetched()
    {
        var handler = new StubHttpMessageHandler(TokenJson);
        var (service, _, _) = CreateService(handler, StoredToken(Now.AddMinutes(30)));

        var tokens = await service.RefreshAsync(
            "a-token-from-an-earlier-request",
            TestContext.Current.CancellationToken);

        Assert.Equal("stored-access", tokens.AccessToken);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SignOutAsync_clears_the_store()
    {
        var (service, store, _) = CreateService(
            new StubHttpMessageHandler(TokenJson),
            StoredToken(Now.AddMinutes(30)));

        await service.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    private static TokenSet StoredToken(DateTimeOffset expiresAt) => new()
    {
        AccessToken = "stored-access",
        RefreshToken = "stored-refresh",
        ExpiresAt = expiresAt,
    };

    private static (AuthService Service, ITokenStore Store, FakeTimeProvider Time) CreateService(
        StubHttpMessageHandler handler,
        TokenSet? stored = null)
    {
        var store = new InMemoryTokenStore(stored);
        var time = new FakeTimeProvider(Now);
        var service = new AuthService(new HttpClient(handler), store, timeProvider: time);

        return (service, store, time);
    }
}

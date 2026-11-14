using System.Net;
using System.Net.Sockets;
using System.Web;
using Galaxy.Protocols.CommunicationService;
using Goggles.Core.Api.Auth;
using Goggles.Core.Api.Embed;
using Goggles.Core.Galaxy;
using Google.Protobuf;

namespace Goggles.Core.Tests.Galaxy;

/// <summary>Plays the game's side of the socket against a stubbed GOG.</summary>
public class CommunicationHandlerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string GameClientId = "52472569771423029";
    private const string GameClientSecret = "game-secret";
    private const ulong GalaxyUserId = 48485266454929432;

    private const string UserDataJson = """
        {"userId":"1","galaxyUserId":"48485266454929432","username":"angelos","isLoggedIn":true}
        """;

    private const string GameTokenJson = """
        {"access_token":"game-access","refresh_token":"game-refresh","expires_in":3600,"user_id":"48485266454929432"}
        """;

    [Fact]
    public async Task Auth_is_answered_with_the_games_own_token_and_the_signed_in_user()
    {
        using var gog = new FakeGog(tokenStatus: HttpStatusCode.OK);
        await using var service = gog.StartService();
        using var game = await ConnectAsync(service);

        await SendAuthAsync(game, sequence: 1);

        var overlay = await ReceiveAsync(game);
        Assert.Equal((uint)MessageType.OverlayStateChangeNotification, overlay.Type);
        Assert.Null(overlay.RespondsTo);

        var reply = await ReceiveAsync(game);
        Assert.Equal((uint)MessageType.AuthInfoResponse, reply.Type);
        Assert.Equal(1u, reply.RespondsTo);

        var body = AuthInfoResponse.Parser.ParseFrom(reply.Payload.Span);
        Assert.Equal("game-refresh", body.RefreshToken);
        Assert.Equal((2UL << 56) | GalaxyUserId, body.UserId);
        Assert.Equal("angelos", body.UserName);
        Assert.Equal(EnvironmentType.EnvironmentProduction, body.EnvironmentType);
    }

    [Fact]
    public async Task The_token_exchange_uses_the_games_credentials_and_leaves_ours_alone()
    {
        using var gog = new FakeGog(tokenStatus: HttpStatusCode.OK);
        await using var service = gog.StartService();
        using var game = await ConnectAsync(service);

        await SendAuthAsync(game, sequence: 1);
        await ReceiveAsync(game);
        await ReceiveAsync(game);

        var tokenRequest = Assert.Single(gog.Requests, uri => uri.Host == "auth.gog.com");
        var query = HttpUtility.ParseQueryString(tokenRequest.Query);
        Assert.Equal(GameClientId, query["client_id"]);
        Assert.Equal(GameClientSecret, query["client_secret"]);
        Assert.Equal("refresh_token", query["grant_type"]);
        Assert.Equal("stored-refresh", query["refresh_token"]);
        Assert.Equal("1", query["without_new_session"]);
        Assert.Null(query["scope"]);

        var stored = await gog.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("stored-refresh", stored!.RefreshToken);
    }

    [Fact]
    public async Task A_game_the_account_does_not_own_is_disconnected_without_signing_out()
    {
        using var gog = new FakeGog(tokenStatus: HttpStatusCode.Forbidden);
        var signInRequired = false;
        gog.Auth.SignInRequired += (_, _) => signInRequired = true;
        await using var service = gog.StartService();
        using var game = await ConnectAsync(service);

        await SendAuthAsync(game, sequence: 1);

        var closed = await GalaxyFraming.ReadAsync(game.GetStream(), TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Null(closed);
        Assert.False(signInRequired);
        Assert.NotNull(await gog.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_exchange_still_identifies_the_user_so_the_game_can_run_offline()
    {
        using var gog = new FakeGog(tokenStatus: HttpStatusCode.ServiceUnavailable);
        await using var service = gog.StartService();
        using var game = await ConnectAsync(service);

        await SendAuthAsync(game, sequence: 1);
        await ReceiveAsync(game);
        var body = AuthInfoResponse.Parser.ParseFrom((await ReceiveAsync(game)).Payload.Span);

        Assert.False(body.HasRefreshToken);
        Assert.Equal((2UL << 56) | GalaxyUserId, body.UserId);
    }

    [Fact]
    public async Task Requests_not_handled_yet_get_no_answer_and_the_connection_carries_on()
    {
        using var gog = new FakeGog(tokenStatus: HttpStatusCode.OK);
        await using var service = gog.StartService();
        using var game = await ConnectAsync(service);

        await GalaxyFraming.WriteAsync(game.GetStream(), new GalaxyMessage
        {
            Sort = CommunicationMessages.Sort,
            Type = (uint)MessageType.GetUserAchievementsRequest,
            Sequence = 1,
        }, TestContext.Current.CancellationToken);
        await SendAuthAsync(game, sequence: 2);

        await ReceiveAsync(game);
        Assert.Equal(2u, (await ReceiveAsync(game)).RespondsTo);
    }

    [Fact]
    public async Task The_user_is_looked_up_once_however_many_games_sign_in()
    {
        using var gog = new FakeGog(tokenStatus: HttpStatusCode.OK);
        await using var service = gog.StartService();

        for (var sequence = 1u; sequence <= 2; sequence++)
        {
            using var game = await ConnectAsync(service);
            await SendAuthAsync(game, sequence);
            await ReceiveAsync(game);
            await ReceiveAsync(game);
        }

        Assert.Single(gog.Requests, uri => uri.Host == "embed.gog.com");
    }

    private static Task SendAuthAsync(TcpClient game, uint sequence) =>
        GalaxyFraming.WriteAsync(game.GetStream(), new GalaxyMessage
        {
            Sort = CommunicationMessages.Sort,
            Type = (uint)MessageType.AuthInfoRequest,
            Sequence = sequence,
            Payload = new AuthInfoRequest
            {
                ClientId = GameClientId,
                ClientSecret = GameClientSecret,
                GamePid = 4242,
            }.ToByteArray(),
        }, TestContext.Current.CancellationToken).AsTask();

    private static async Task<TcpClient> ConnectAsync(CommunicationService service)
    {
        var client = new TcpClient();
        await client.ConnectAsync(service.LocalEndpoint, TestContext.Current.CancellationToken);
        return client;
    }

    private static async Task<GalaxyMessage> ReceiveAsync(TcpClient game)
    {
        var message = await GalaxyFraming.ReadAsync(game.GetStream(), TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        return message ?? throw new EndOfStreamException("The service closed the connection.");
    }

    /// <summary>auth.gog.com and embed.gog.com behind one stub, plus a signed-in token store.</summary>
    private sealed class FakeGog : IDisposable
    {
        private readonly HttpClient _http;

        public FakeGog(HttpStatusCode tokenStatus)
        {
            var handler = new StubHttpMessageHandler((request, _) => request.RequestUri!.Host switch
            {
                "auth.gog.com" => StubHttpMessageHandler.Json(
                    tokenStatus == HttpStatusCode.OK ? GameTokenJson : """{"error":"refused"}""",
                    tokenStatus),
                "embed.gog.com" => StubHttpMessageHandler.Json(UserDataJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            Requests = handler.RequestedUris;
            _http = new HttpClient(handler);

            Store = new InMemoryTokenStore(new TokenSet
            {
                AccessToken = "stored-access",
                RefreshToken = "stored-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            Auth = new AuthService(_http, Store);
        }

        public List<Uri> Requests { get; }

        public ITokenStore Store { get; }

        public AuthService Auth { get; }

        public CommunicationService StartService()
        {
            var handler = new CommunicationHandler(Auth, new EmbedClient(_http));
            var service = new CommunicationService(handler.HandleAsync, port: 0);
            service.Start();
            return service;
        }

        public void Dispose() => _http.Dispose();
    }
}

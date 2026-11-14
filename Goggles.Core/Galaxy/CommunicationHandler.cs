using System.Net;
using Galaxy.Protocols.CommunicationService;
using Goggles.Core.Api;
using Goggles.Core.Api.Auth;
using Goggles.Core.Api.Embed;
using Google.Protobuf;

namespace Goggles.Core.Galaxy;

/// <summary>
/// Answers what a game's Galaxy SDK asks the <see cref="CommunicationService"/>. Phase 2 answers
/// <c>AUTH_INFO_REQUEST</c> only; everything else is logged and left unanswered, which is also how
/// Comet treats requests it does not implement (the SDK then gives up on that call).
/// </summary>
public sealed class CommunicationHandler
{
    // Galaxy ids carry their kind in the top byte: 1 marks a lobby, 2 a user (Comet's IDType).
    private const ulong UserIdKind = 2UL << 56;

    private readonly AuthService _auth;
    private readonly EmbedClient _embed;
    private readonly TextWriter? _log;
    private readonly SemaphoreSlim _userGate = new(1, 1);
    private SignedInUser? _user;

    public CommunicationHandler(AuthService auth, EmbedClient embed, TextWriter? log = null)
    {
        _auth = auth;
        _embed = embed;
        _log = log;
    }

    public async ValueTask<GalaxyMessage?> HandleAsync(
        GalaxyConnection connection,
        GalaxyMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Sort != CommunicationMessages.Sort)
        {
            return null;
        }

        return (MessageType)request.Type switch
        {
            MessageType.AuthInfoRequest => await AuthInfoAsync(connection, request, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<GalaxyMessage?> AuthInfoAsync(
        GalaxyConnection connection,
        GalaxyMessage request,
        CancellationToken cancellationToken)
    {
        var body = AuthInfoRequest.Parser.ParseFrom(request.Payload.Span);
        var user = await GetUserAsync(cancellationToken).ConfigureAwait(false);

        var response = new AuthInfoResponse
        {
            EnvironmentType = EnvironmentType.EnvironmentProduction,
            Region = Region.WorldWide,
            UserId = UserIdKind | user.GalaxyUserId,
            UserName = user.UserName,
        };

        try
        {
            var token = await _auth
                .GetTokenForClientAsync(body.ClientId, body.ClientSecret, body.Openid, cancellationToken)
                .ConfigureAwait(false);
            response.RefreshToken = token.RefreshToken;
        }
        catch (GogAuthException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            // GOG will not issue this game's credentials to this account — it is not owned. Comet
            // closes the connection here rather than replying, so the SDK stops asking.
            throw new GalaxyConnectionRefusedException(
                $"GOG refused a token for client {body.ClientId}; the account does not own it.", exception);
        }
        catch (Exception exception) when (exception is GogAuthException or HttpRequestException)
        {
            // Offline, or GOG is having trouble. Reply without a token, as Comet does, so the game
            // carries on signed-out-but-identified rather than retrying forever.
            _log?.WriteLine($"Galaxy: connection {connection.Id} could not get a token for client {body.ClientId}: {exception.Message}");
        }

        // Comet tells the SDK the overlay is up before answering auth. Goggles has no overlay; the
        // notification is sent because SDKs are known to work after it, not because it is true.
        await connection.SendAsync(new GalaxyMessage
        {
            Sort = CommunicationMessages.Sort,
            Type = (uint)MessageType.OverlayStateChangeNotification,
            Payload = new OverlayStateChangeNotification
            {
                OverlayState = OverlayStateChangeNotification.Types.OverlayState.Initialized,
            }.ToByteArray(),
        }, cancellationToken).ConfigureAwait(false);

        return new GalaxyMessage
        {
            Sort = CommunicationMessages.Sort,
            Type = (uint)MessageType.AuthInfoResponse,
            Payload = response.ToByteArray(),
        };
    }

    /// <summary>Who is signed in, fetched once per handler; a failed fetch is retried next time.</summary>
    private async Task<SignedInUser> GetUserAsync(CancellationToken cancellationToken)
    {
        if (_user is { } cached)
        {
            return cached;
        }

        await _userGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_user is null)
            {
                var data = await _embed.GetUserDataAsync(cancellationToken).ConfigureAwait(false);
                if (!ulong.TryParse(data.GalaxyUserId, out var galaxyUserId))
                {
                    throw new GogApiException("userData.json carried no usable galaxyUserId.");
                }

                _user = new SignedInUser(galaxyUserId, data.Username ?? "");
            }

            return _user;
        }
        finally
        {
            _userGate.Release();
        }
    }

    private sealed record SignedInUser(ulong GalaxyUserId, string UserName);
}

/// <summary>
/// Thrown by a <see cref="GalaxyMessageHandler"/> to end the game's connection instead of replying.
/// </summary>
public sealed class GalaxyConnectionRefusedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

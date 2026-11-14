using Galaxy.Protocols.CommunicationService;
using Goggles.Core.Galaxy;
using Google.Protobuf;

namespace Goggles.Core.Tests.Galaxy;

public class CommunicationMessagesTests
{
    private static GalaxyMessage AuthInfo() => new()
    {
        Sort = CommunicationMessages.Sort,
        Type = (uint)MessageType.AuthInfoRequest,
        Sequence = 3,
        Payload = new AuthInfoRequest
        {
            ClientId = "53987234598432",
            ClientSecret = "the-games-secret",
            GamePid = 4321,
        }.ToByteArray(),
    };

    [Fact]
    public void Messages_are_named_as_the_protocol_names_them()
    {
        Assert.Equal("AUTH_INFO_REQUEST", CommunicationMessages.NameOf(AuthInfo()));
    }

    [Fact]
    public void The_aliased_session_type_is_named_for_games_rather_than_the_overlay()
    {
        var message = new GalaxyMessage { Sort = 1, Type = (uint)MessageType.StartGameSessionRequest };

        Assert.Equal("START_GAME_SESSION_REQUEST", CommunicationMessages.NameOf(message));
    }

    [Fact]
    public void Other_subprotocols_are_named_by_number()
    {
        Assert.Equal("sort 6 type 2", CommunicationMessages.NameOf(new GalaxyMessage { Sort = 6, Type = 2 }));
    }

    [Fact]
    public void A_description_decodes_the_body_but_never_shows_the_client_secret()
    {
        var description = CommunicationMessages.Describe(AuthInfo());

        Assert.StartsWith("AUTH_INFO_REQUEST seq=3", description, StringComparison.Ordinal);
        Assert.Contains("53987234598432", description, StringComparison.Ordinal);
        Assert.Contains("4321", description, StringComparison.Ordinal);
        Assert.DoesNotContain("the-games-secret", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacting_replaces_credentials_and_keeps_everything_else()
    {
        var redacted = CommunicationMessages.Redact(AuthInfo());
        var body = AuthInfoRequest.Parser.ParseFrom(redacted.Payload.Span);

        Assert.Equal("[redacted]", body.ClientSecret);
        Assert.Equal("53987234598432", body.ClientId);
        Assert.Equal(4321u, body.GamePid);
        Assert.Equal(3u, redacted.Sequence);
    }

    [Fact]
    public void A_refresh_token_handed_to_a_game_is_redacted_too()
    {
        var message = new GalaxyMessage
        {
            Sort = 1,
            Type = (uint)MessageType.AuthInfoResponse,
            Payload = new AuthInfoResponse { RefreshToken = "secret-refresh", UserName = "angelos" }.ToByteArray(),
        };

        var body = AuthInfoResponse.Parser.ParseFrom(CommunicationMessages.Redact(message).Payload.Span);

        Assert.Equal("[redacted]", body.RefreshToken);
        Assert.Equal("angelos", body.UserName);
    }

    [Fact]
    public void A_body_with_nothing_to_redact_is_returned_untouched()
    {
        var message = new GalaxyMessage
        {
            Sort = 1,
            Type = (uint)MessageType.LibraryInfoRequest,
            Payload = new LibraryInfoRequest { CompilerVersion = "19" }.ToByteArray(),
        };

        Assert.Same(message, CommunicationMessages.Redact(message));
    }

    [Fact]
    public void A_body_that_cannot_be_decoded_is_reported_by_size()
    {
        var message = new GalaxyMessage { Sort = 7, Type = 1, Payload = new byte[] { 1, 2, 3 } };

        Assert.EndsWith("<3 bytes, not decoded>", CommunicationMessages.Describe(message), StringComparison.Ordinal);
    }
}

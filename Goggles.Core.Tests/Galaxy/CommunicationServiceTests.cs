using System.Net;
using System.Net.Sockets;
using Goggles.Core.Galaxy;

namespace Goggles.Core.Tests.Galaxy;

/// <summary>Drives the service over a real loopback socket, playing the game's side.</summary>
public class CommunicationServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_reply_quotes_the_request_sequence_and_keeps_its_sort()
    {
        await using var service = Start((_, request, _) =>
            ValueTask.FromResult<GalaxyMessage?>(new GalaxyMessage
            {
                Sort = 99, // overwritten: a reply always travels in the request's subprotocol
                Type = request.Type + 1,
                Payload = new byte[] { 42 },
            }));
        using var game = await ConnectAsync(service);

        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 1, Sequence = 7 });
        var reply = await ReceiveAsync(game);

        Assert.Equal(1u, reply.Sort);
        Assert.Equal(2u, reply.Type);
        Assert.Equal(7u, reply.RespondsTo);
        Assert.Equal(new byte[] { 42 }, reply.Payload.ToArray());
    }

    [Fact]
    public async Task A_handler_returning_null_sends_nothing_and_the_connection_carries_on()
    {
        await using var service = Start((_, request, _) =>
            ValueTask.FromResult<GalaxyMessage?>(request.Sequence == 2
                ? new GalaxyMessage { Sort = 1, Type = 4 }
                : null));
        using var game = await ConnectAsync(service);

        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 1, Sequence = 1 });
        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 3, Sequence = 2 });

        // The first reply to arrive answers the second request; nothing was sent for the first.
        Assert.Equal(2u, (await ReceiveAsync(game)).RespondsTo);
    }

    [Fact]
    public async Task A_handler_that_throws_costs_only_that_request()
    {
        await using var service = Start((_, request, _) => request.Sequence == 1
            ? throw new InvalidOperationException("boom")
            : ValueTask.FromResult<GalaxyMessage?>(new GalaxyMessage { Sort = 1, Type = 4 }));
        using var game = await ConnectAsync(service);

        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 1, Sequence = 1 });
        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 3, Sequence = 2 });

        Assert.Equal(2u, (await ReceiveAsync(game)).RespondsTo);
    }

    [Fact]
    public async Task Unreadable_bytes_close_only_that_connection()
    {
        await using var service = Start((_, _, _) =>
            ValueTask.FromResult<GalaxyMessage?>(new GalaxyMessage { Sort = 1, Type = 2 }));
        using var broken = await ConnectAsync(service);
        using var healthy = await ConnectAsync(service);

        await broken.GetStream().WriteAsync(new byte[] { 0, 3, 0xFF, 0xFF, 0xFF }, TestContext.Current.CancellationToken);
        var closed = await broken.GetStream().ReadAsync(new byte[1], TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, closed);

        await SendAsync(healthy, new GalaxyMessage { Sort = 1, Type = 1, Sequence = 5 });
        Assert.Equal(5u, (await ReceiveAsync(healthy)).RespondsTo);
    }

    [Fact]
    public async Task The_handler_can_push_a_message_before_replying()
    {
        await using var service = Start(async (connection, _, cancellationToken) =>
        {
            await connection.SendAsync(new GalaxyMessage { Sort = 1, Type = 58 }, cancellationToken);
            return new GalaxyMessage { Sort = 1, Type = 4 };
        });
        using var game = await ConnectAsync(service);

        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 3, Sequence = 1 });

        var pushed = await ReceiveAsync(game);
        var reply = await ReceiveAsync(game);
        Assert.Equal(58u, pushed.Type);
        Assert.Null(pushed.RespondsTo);
        Assert.Equal(4u, reply.Type);
        Assert.Equal(1u, reply.RespondsTo);
    }

    [Fact]
    public async Task It_listens_on_loopback_only()
    {
        await using var service = Start((_, _, _) => ValueTask.FromResult<GalaxyMessage?>(null));

        Assert.Equal(IPAddress.Loopback, service.LocalEndpoint.Address);
    }

    [Fact]
    public async Task A_taken_port_is_reported_by_name()
    {
        await using var first = Start((_, _, _) => ValueTask.FromResult<GalaxyMessage?>(null));
        await using var second = new CommunicationService(
            (_, _, _) => ValueTask.FromResult<GalaxyMessage?>(null),
            first.LocalEndpoint.Port);

        var exception = Assert.Throws<InvalidOperationException>(second.Start);
        Assert.Contains(first.LocalEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
    }

    [Fact]
    public async Task Disposing_with_a_game_still_connected_does_not_hang()
    {
        var service = Start((_, _, _) => ValueTask.FromResult<GalaxyMessage?>(null));
        using var game = await ConnectAsync(service);
        await SendAsync(game, new GalaxyMessage { Sort = 1, Type = 1, Sequence = 1 });

        await service.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static CommunicationService Start(GalaxyMessageHandler handler)
    {
        var service = new CommunicationService(handler, port: 0);
        service.Start();
        return service;
    }

    private static async Task<TcpClient> ConnectAsync(CommunicationService service)
    {
        var client = new TcpClient();
        await client.ConnectAsync(service.LocalEndpoint, TestContext.Current.CancellationToken);
        return client;
    }

    private static Task SendAsync(TcpClient game, GalaxyMessage message) =>
        GalaxyFraming.WriteAsync(game.GetStream(), message, TestContext.Current.CancellationToken).AsTask();

    private static async Task<GalaxyMessage> ReceiveAsync(TcpClient game)
    {
        var message = await GalaxyFraming.ReadAsync(game.GetStream(), TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        return message ?? throw new EndOfStreamException("The service closed the connection.");
    }
}

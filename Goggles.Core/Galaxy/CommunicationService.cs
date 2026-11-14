using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Goggles.Core.Galaxy;

/// <summary>
/// Handles one message from a game. Return a reply to send it back, or null to send nothing; the
/// service fills in the reply's <see cref="GalaxyMessage.Sort"/> and
/// <see cref="GalaxyMessage.RespondsTo"/>.
/// </summary>
public delegate ValueTask<GalaxyMessage?> GalaxyMessageHandler(
    GalaxyConnection connection,
    GalaxyMessage request,
    CancellationToken cancellationToken);

/// <summary>
/// Goggles' stand-in for GOG Galaxy's local Communication Service: the socket a game's Galaxy SDK
/// connects to for achievements, stats and the rest. Galaxy normally provides it; without it, a game
/// launched outside Galaxy has nobody to report to.
/// </summary>
/// <remarks>
/// Binds loopback only — the protocol carries tokens and has no authentication of its own, so it must
/// never be reachable from another machine. Messages on one connection are handled in order; nothing
/// so far says the SDK pipelines requests in a way that needs more.
/// </remarks>
public sealed class CommunicationService : IAsyncDisposable
{
    /// <summary>The port Galaxy's SDK connects to. Fixed on the game's side, so not negotiable here.</summary>
    public const int DefaultPort = 9977;

    private readonly TcpListener _listener;
    private readonly GalaxyMessageHandler _handler;
    private readonly TextWriter? _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private Task? _acceptLoop;
    private int _nextConnectionId;

    public CommunicationService(GalaxyMessageHandler handler, int port = DefaultPort, TextWriter? log = null)
    {
        _handler = handler;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Where the service listens; useful when constructed with port 0.</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <exception cref="InvalidOperationException">The port is taken — usually by Galaxy or Comet.</exception>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"Port {((IPEndPoint)_listener.LocalEndpoint).Port} is already in use — is GOG Galaxy or Comet running?",
                exception);
        }

        _acceptLoop = AcceptLoopAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                                  || (exception is SocketException && cancellationToken.IsCancellationRequested))
            {
                return;
            }

            var id = Interlocked.Increment(ref _nextConnectionId);
            _connections[id] = ServeAsync(id, client, cancellationToken);
        }
    }

    private async Task ServeAsync(int id, TcpClient client, CancellationToken cancellationToken)
    {
        // Let the accept loop register this task before it can remove itself.
        await Task.Yield();

        using (client)
        await using (var connection = new GalaxyConnection(id, client))
        {
            _log?.WriteLine($"Galaxy: connection {id} opened from {connection.RemoteEndpoint}");
            try
            {
                await ServeMessagesAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
            {
                // A game that exits mid-message, or sends something unreadable, ends its own connection
                // and nothing else.
                _log?.WriteLine($"Galaxy: connection {id} dropped: {exception.Message}");
            }
            finally
            {
                _log?.WriteLine($"Galaxy: connection {id} closed");
                _connections.TryRemove(id, out _);
            }
        }
    }

    private async Task ServeMessagesAsync(GalaxyConnection connection, CancellationToken cancellationToken)
    {
        while (await GalaxyFraming.ReadAsync(connection.Stream, cancellationToken).ConfigureAwait(false) is { } request)
        {
            GalaxyMessage? reply;
            try
            {
                reply = await _handler(connection, request, cancellationToken).ConfigureAwait(false);
            }
            catch (GalaxyConnectionRefusedException exception)
            {
                _log?.WriteLine($"Galaxy: connection {connection.Id} refused: {exception.Message}");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One failing request must not cost the game its connection, or every later call fails too.
                _log?.WriteLine(
                    $"Galaxy: connection {connection.Id} failed to handle sort {request.Sort} type {request.Type}: {exception}");
                continue;
            }

            if (reply is not null)
            {
                await connection.SendAsync(
                    reply with { Sort = request.Sort, RespondsTo = request.Sequence },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>One game's connection to the <see cref="CommunicationService"/>.</summary>
public sealed class GalaxyConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    internal GalaxyConnection(int id, TcpClient client)
    {
        Id = id;
        RemoteEndpoint = client.Client.RemoteEndPoint;
        Stream = client.GetStream();
    }

    /// <summary>Numbers connections in the order they opened, for logs.</summary>
    public int Id { get; }

    public EndPoint? RemoteEndpoint { get; }

    internal NetworkStream Stream { get; }

    /// <summary>
    /// Sends a message the game did not ask for — Galaxy pushes notifications as well as replying.
    /// Writes are serialised, so a push cannot interleave with a reply.
    /// </summary>
    public async ValueTask SendAsync(GalaxyMessage message, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await GalaxyFraming.WriteAsync(Stream, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return Stream.DisposeAsync();
    }
}

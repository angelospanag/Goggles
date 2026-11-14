using System.Net;
using Goggles.Core.Api.Auth;
using Goggles.Core.Api.ContentSystem;
using Goggles.Core.Api.Embed;
using Goggles.Core.Api.Gameplay;
using Goggles.Core.Downloads;
using Goggles.Core.Library;
using Goggles.Core.Manifests;

namespace Goggles.Core;

/// <summary>
/// Everything Goggles talks to, wired together: which clients are authenticated, which are not, and
/// which handler sits in front of each. Built once per application run.
/// </summary>
/// <remarks>
/// This is a composition root rather than a service locator — take what you need from it at start-up
/// and pass those on. It exists so the desktop app and the CLI cannot drift apart in how the stack
/// is assembled.
/// </remarks>
public sealed class GogSession : IDisposable
{
    private readonly HttpClient _authClient;
    private readonly HttpClient _embedClient;
    private readonly HttpClient _gameplayClient;
    private readonly HttpClient _contentSystemClient;
    private readonly HttpClient _cdnClient;

    private GogSession(
        ITokenStore store,
        DownloadOptions? downloadOptions,
        Func<HttpMessageHandler, HttpMessageHandler>? decorateTransport)
    {
        Tokens = store;

        // The token endpoint is unauthenticated, so AuthService gets a plain client of its own —
        // routing it through GogAuthHandler would recurse.
        _authClient = CreateClient(decorateTransport);
        Auth = new AuthService(_authClient, store);

        _embedClient = CreateClient(decorateTransport, new GogAuthHandler(Auth));
        Embed = new EmbedClient(_embedClient);
        Library = new LibrarySyncService(Embed);

        _gameplayClient = CreateClient(decorateTransport, new GogAuthHandler(Auth));
        Gameplay = new GameplayClient(_gameplayClient);
        Stats = new GameStatsCache(Gameplay);

        _contentSystemClient = CreateClient(decorateTransport, new GogAuthHandler(Auth));
        Builds = new BuildsClient(_contentSystemClient);
        SecureLinks = new SecureLinkClient(_contentSystemClient);

        // The CDN serves public, signed URLs: no bearer token belongs on these requests.
        _cdnClient = CreateClient(decorateTransport: null);
        Cdn = new CdnClient(_cdnClient);

        Manifests = new ManifestResolver(Builds, Cdn);
        Downloads = new DownloadEngine(_cdnClient, new SecureLinkProvider(SecureLinks), downloadOptions);
    }

    public ITokenStore Tokens { get; }

    public AuthService Auth { get; }

    public EmbedClient Embed { get; }

    public LibrarySyncService Library { get; }

    /// <summary>The last library sync on disk. Nothing is read or written until it is asked to.</summary>
    public LibraryCache LibraryCache { get; } = new();

    public GameplayClient Gameplay { get; }

    public GameStatsCache Stats { get; }

    public BuildsClient Builds { get; }

    public SecureLinkClient SecureLinks { get; }

    public CdnClient Cdn { get; }

    public ManifestResolver Manifests { get; }

    public DownloadEngine Downloads { get; }

    /// <param name="store">Where credentials live. Defaults to <see cref="FileTokenStore"/>.</param>
    /// <param name="downloadOptions">Download tuning, or null for the defaults.</param>
    /// <param name="decorateTransport">
    /// Wraps the transport of every authenticated client — used by the CLI to dump raw responses.
    /// </param>
    public static GogSession Create(
        ITokenStore? store = null,
        DownloadOptions? downloadOptions = null,
        Func<HttpMessageHandler, HttpMessageHandler>? decorateTransport = null) =>
        new(store ?? new FileTokenStore(), downloadOptions, decorateTransport);

    public void Dispose()
    {
        _authClient.Dispose();
        _embedClient.Dispose();
        _gameplayClient.Dispose();
        _contentSystemClient.Dispose();
        _cdnClient.Dispose();
        Stats.Dispose();
    }

    private static HttpClient CreateClient(
        Func<HttpMessageHandler, HttpMessageHandler>? decorateTransport,
        DelegatingHandler? outer = null)
    {
        HttpMessageHandler transport = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (decorateTransport is not null)
        {
            transport = decorateTransport(transport);
        }

        if (outer is not null)
        {
            outer.InnerHandler = transport;
            transport = outer;
        }

        var client = new HttpClient(transport);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Goggles/0.1");

        return client;
    }
}

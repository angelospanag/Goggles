using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Goggles.Core.Api.Gameplay.Models;

namespace Goggles.Core.Api.Gameplay;

/// <summary>
/// Talks to <c>gameplay.gog.com</c> — achievements and play sessions. Unlike
/// <c>embed.gog.com</c> and content-system, this host has no official public documentation; the
/// endpoint shapes here are reconstructed from community GOG-client reimplementations (gogapidocs,
/// Heroic Games Launcher). Reads use the same bearer token as every other GOG host Goggles talks
/// to — confirmed against Heroic's implementation — so no per-game client credential exchange is
/// needed.
/// </summary>
/// <remarks>
/// Every endpoint here needs a signed-in user, so the supplied <see cref="HttpClient"/> is expected
/// to be wired through <see cref="Auth.GogAuthHandler"/>, same as <see cref="Embed.EmbedClient"/>.
/// </remarks>
public sealed class GameplayClient
{
    /// <summary>
    /// Caps how many pages <see cref="GetAchievementsAsync"/> will walk via <c>page_token</c>, as a
    /// defensive limit against an unexpected infinite-token response from an undocumented host.
    /// </summary>
    public const int MaxAchievementPages = 20;

    // Confirmed against a live response (see Goggles.Cli's `stats` command): unlike embed.gog.com's
    // camelCase, gameplay.gog.com answers in snake_case ("total_count", "achievement_key", ...).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public GameplayClient(HttpClient http) => _http = http;

    /// <summary>
    /// Returns every achievement for <paramref name="productId"/> and this user's unlock state for
    /// each, walking pages until exhausted or <see cref="MaxAchievementPages"/> is reached.
    /// </summary>
    public async Task<Achievements> GetAchievementsAsync(
        long productId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var total = 0;
        var items = new List<Achievement>();
        string? pageToken = null;

        for (var page = 0; page < MaxAchievementPages; page++)
        {
            var uri = BuildUri($"clients/{productId}/users/{userId}/achievements", pageToken);
            var raw = await GetAsync<RawAchievementsPage>(uri, cancellationToken).ConfigureAwait(false);

            total = raw.TotalCount;
            items.AddRange(raw.Items.Select(Map));

            // Confirmed against a live response: page_token can be a non-empty sentinel ("0") even
            // when every item already came back in this page, so an empty token alone isn't a
            // reliable "no more pages" signal — stop once the running total reaches what GOG
            // reported, too.
            if (string.IsNullOrEmpty(raw.PageToken) || items.Count >= total)
            {
                break;
            }

            pageToken = raw.PageToken;
        }

        return new Achievements { Total = total, Items = items };
    }

    /// <summary>
    /// Returns this user's total play time for <paramref name="productId"/>, if the response
    /// contains a value this client recognizes. See <see cref="RawSessionsResponse"/> for why this
    /// degrades gracefully instead of throwing on an unrecognized shape.
    /// </summary>
    public async Task<SessionSummary> GetSessionSummaryAsync(
        long productId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Confirmed against a live account: unlike achievements, this endpoint is under
        // "games/", not "clients/" — "clients/{id}" is a different lookup (a Galaxy SDK
        // client id, not a product id) and 404s with "Unable to find Client by clientId#…".
        var uri = BuildUri($"games/{productId}/users/{userId}/sessions");
        var raw = await GetAsync<RawSessionsResponse>(uri, cancellationToken).ConfigureAwait(false);

        if (raw.TimeSum is { } timeSum)
        {
            return new SessionSummary { Available = true, Minutes = timeSum };
        }

        if (raw.Sessions is { Count: > 0 } sessions)
        {
            return new SessionSummary { Available = true, Minutes = sessions.Sum(s => s.Time) };
        }

        return new SessionSummary { Available = false };
    }

    private static Achievement Map(RawAchievementItem item)
    {
        // Confirmed against a live response: GOG sends "2026-07-31T15:36:58+0000" — a numeric
        // zone offset with no colon. DateTimeOffset.TryParse handles that fine, but only reliably
        // so under an invariant culture — pinned explicitly rather than trusting the host's.
        DateTimeOffset? unlockedAt = null;
        if (!string.IsNullOrEmpty(item.DateUnlocked) &&
            DateTimeOffset.TryParse(
                item.DateUnlocked, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            unlockedAt = parsed;
        }

        return new Achievement
        {
            Key = item.AchievementKey ?? string.Empty,
            Name = item.Name,
            Description = item.Description,
            ImageUrlUnlocked = item.ImageUrlUnlocked,
            ImageUrlLocked = item.ImageUrlLocked,
            Visible = item.Visible,
            UnlockedAt = unlockedAt,
        };
    }

    private async Task<T> GetAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GogApiException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                response.StatusCode,
                body);
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                   ?? throw new GogApiException($"GOG returned an empty body for {uri}.");
        }
        catch (JsonException exception)
        {
            throw new GogApiException(
                $"GET {uri} returned a body that could not be read as {typeof(T).Name}: {exception.Message}");
        }
    }

    private static Uri BuildUri(string path, string? pageToken = null)
    {
        var query = string.IsNullOrEmpty(pageToken) ? "" : $"?page_token={Uri.EscapeDataString(pageToken)}";
        return new Uri(GogHosts.Gameplay, path + query);
    }

    /// <summary>One page of <c>gameplay.gog.com/clients/{id}/users/{id}/achievements</c>.</summary>
    private sealed record RawAchievementsPage
    {
        public int TotalCount { get; init; }

        public string? PageToken { get; init; }

        public IReadOnlyList<RawAchievementItem> Items { get; init; } = [];
    }

    private sealed record RawAchievementItem
    {
        public string? AchievementKey { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }

        public string? ImageUrlUnlocked { get; init; }

        public string? ImageUrlLocked { get; init; }

        public bool Visible { get; init; }

        public string? DateUnlocked { get; init; }
    }

    /// <summary>
    /// A lenient superset of every session-summary shape observed in community client
    /// reimplementations — this endpoint has no official documentation, so decoding tries each
    /// candidate field in turn rather than assuming one is correct.
    /// </summary>
    private sealed record RawSessionsResponse
    {
        /// <summary>If present, assumed to already be the total in minutes.</summary>
        public int? TimeSum { get; init; }

        /// <summary>If present without <see cref="TimeSum"/>, summed to produce a total.</summary>
        public IReadOnlyList<RawSession>? Sessions { get; init; }
    }

    private sealed record RawSession
    {
        public int Time { get; init; }
    }
}

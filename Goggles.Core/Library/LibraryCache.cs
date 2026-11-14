using System.Text.Json;

namespace Goggles.Core.Library;

/// <summary>
/// The last library sync, kept on disk so the library can be shown the moment the app opens and
/// refreshed behind it. Stored as one JSON file beside the tokens.
/// </summary>
/// <remarks>
/// A cache, not a record: anything wrong with the file — missing, corrupt, an older format, another
/// account's — reads as "nothing cached", and the caller simply syncs in full as it would anyway.
/// It holds the user's owned library, so it is deleted on sign-out like the tokens.
/// </remarks>
public sealed class LibraryCache
{
    /// <summary>
    /// How long a cached library may be trusted on its checksum alone. GOG's library checksum covers
    /// what is owned, not every field shown (a community rating can change without it), so past this
    /// age the library is fetched in full again even if the checksum still matches.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>Bumped whenever the stored shape changes, so an old file is ignored rather than misread.</summary>
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeProvider _time;

    public LibraryCache(string? path = null, TimeProvider? timeProvider = null)
    {
        Path = path ?? DefaultPath();
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Path { get; }

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Goggles",
        "library.json");

    /// <summary>The cached library for <paramref name="userId"/>, or null if there is none to trust.</summary>
    public async Task<CachedLibrary?> LoadAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            await using var stream = File.OpenRead(Path);
            var stored = await JsonSerializer.DeserializeAsync<CachedLibrary>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return stored is { Version: FormatVersion } && stored.UserId == userId ? stored : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores a sync's result for <paramref name="userId"/>. Only a result that carries the library
    /// is worth keeping; an unchanged one leaves the existing file as it is.
    /// </summary>
    public async Task SaveAsync(string userId, LibrarySyncResult result, CancellationToken cancellationToken = default)
    {
        if (!result.Changed)
        {
            return;
        }

        var stored = new CachedLibrary
        {
            Version = FormatVersion,
            UserId = userId,
            Checksum = result.Checksum,
            AccountName = result.AccountName,
            SavedAt = _time.GetUtcNow(),
            Games = result.Games,
        };

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>The checksum to hand <see cref="LibrarySyncService.SyncAsync"/>: null once too old to trust.</summary>
    public string? ChecksumWorthTrusting(CachedLibrary? cached) =>
        cached is not null && _time.GetUtcNow() - cached.SavedAt < MaxAge ? cached.Checksum : null;

    public void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable to us means unreadable to the next account too: LoadAsync treats it as empty.
        }
    }
}

/// <summary>What <see cref="LibraryCache"/> keeps on disk.</summary>
public sealed record CachedLibrary
{
    public int Version { get; init; }

    public required string UserId { get; init; }

    public string? Checksum { get; init; }

    public string? AccountName { get; init; }

    public DateTimeOffset SavedAt { get; init; }

    public IReadOnlyList<OwnedGame> Games { get; init; } = [];
}

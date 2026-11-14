namespace Goggles.Core.Library;

/// <summary>Outcome of a library sync.</summary>
public sealed record LibrarySyncResult
{
    /// <summary>
    /// False when the sync short-circuited because GOG's games checksum matched the one supplied,
    /// in which case <see cref="Games"/> is empty and any cached copy is still current.
    /// </summary>
    public required bool Changed { get; init; }

    /// <summary>The checksum to hand back to the next sync. Null if GOG did not report one.</summary>
    public string? Checksum { get; init; }

    /// <summary>
    /// Who the library belongs to. Carried here because the sync already fetches the user data the
    /// checksum comes from, and asking twice would be a wasted round trip.
    /// </summary>
    public string? AccountName { get; init; }

    public IReadOnlyList<OwnedGame> Games { get; init; } = [];

    /// <summary>Pages fetched, useful for logging how expensive a sync was.</summary>
    public int PagesFetched { get; init; }

    public static LibrarySyncResult Unchanged(string? checksum, string? accountName = null) =>
        new() { Changed = false, Checksum = checksum, AccountName = accountName };
}

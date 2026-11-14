namespace Goggles.Core.Api.ContentSystem;

/// <summary>
/// GOG shards content by hash into two levels of two hex characters — <c>ab/cd/abcd…</c> — for both
/// metadata blobs and depot chunks. Undocumented; derived from observed URLs.
/// </summary>
public static class GalaxyPath
{
    public static string Shard(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (hash.Length < 4)
        {
            throw new ArgumentException($"'{hash}' is too short to be a content hash.", nameof(hash));
        }

        return $"{hash[..2]}/{hash[2..4]}/{hash}";
    }
}

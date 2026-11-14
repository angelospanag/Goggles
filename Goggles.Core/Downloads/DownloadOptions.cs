namespace Goggles.Core.Downloads;

public sealed record DownloadOptions
{
    /// <summary>Chunks fetched at once. GOG tolerates a handful; this is not a place to be greedy.</summary>
    public int MaxParallelChunks { get; init; } = 4;

    /// <summary>Attempts per chunk before giving up, rotating CDN endpoints between attempts.</summary>
    public int MaxAttemptsPerChunk { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Skip chunks already present and intact on disk. Costs one read and one MD5 per chunk of a
    /// pre-existing file, which is far cheaper than fetching it again.
    /// </summary>
    public bool Resume { get; init; } = true;

    /// <summary>
    /// Re-read every completed file to check its SHA-256. Chunks are already verified individually,
    /// so this is a belt-and-braces pass that doubles the disk reads.
    /// </summary>
    public bool VerifyFileHashes { get; init; }
}

/// <summary>Progress snapshot, reported as each chunk settles.</summary>
public sealed record DownloadProgress
{
    public required long BytesCompleted { get; init; }

    public required long TotalBytes { get; init; }

    public required int ChunksCompleted { get; init; }

    public required int TotalChunks { get; init; }

    /// <summary>Bytes that did not need fetching because resume matched them.</summary>
    public long BytesSkipped { get; init; }

    public string? CurrentPath { get; init; }

    public double Fraction => TotalBytes == 0 ? 1 : (double)BytesCompleted / TotalBytes;
}

/// <summary>What a completed download actually did.</summary>
public sealed record DownloadReport
{
    public required int FilesWritten { get; init; }

    public required int ChunksDownloaded { get; init; }

    public required int ChunksSkipped { get; init; }

    public required long BytesWritten { get; init; }

    public required TimeSpan Duration { get; init; }
}

namespace Goggles.Core.Manifests;

/// <summary>A depot metadata blob: the files one depot contains and how they are chunked.</summary>
public sealed record DepotManifest
{
    public int Version { get; init; }

    public DepotContents? Depot { get; init; }
}

public sealed record DepotContents
{
    public IReadOnlyList<DepotItem> Items { get; init; } = [];

    /// <summary>
    /// Present on most depots. Its purpose is not yet established — small files are believed to be
    /// packed together here, but Goggles does not rely on it.
    /// </summary>
    public DepotItem? SmallFilesContainer { get; init; }
}

public sealed record DepotItem
{
    /// <summary>Depot-relative, and backslash-separated on Windows depots.</summary>
    public string? Path { get; init; }

    /// <summary>Observed: <c>DepotFile</c>, <c>DepotDirectory</c>, <c>DepotLink</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Whole-file checksum. Absent on some entries.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Observed: <c>executable</c>, <c>support</c>, <c>hidden</c>.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    public IReadOnlyList<DepotChunk> Chunks { get; init; } = [];
}

/// <summary>
/// One 10 MiB (uncompressed) slice of a file. Chunks are addressed on the CDN by
/// <see cref="CompressedMd5"/>, and verified on both sides of decompression.
/// </summary>
public sealed record DepotChunk
{
    public string? Md5 { get; init; }

    public long Size { get; init; }

    public string? CompressedMd5 { get; init; }

    public long CompressedSize { get; init; }
}

namespace Goggles.Core.Manifests;

/// <summary>
/// A build flattened into exactly what the download engine needs: one entry per file, with chunks
/// carrying the offset they occupy in it.
/// </summary>
public sealed record ResolvedBuild
{
    public required string BuildId { get; init; }

    public required string Platform { get; init; }

    /// <summary>Folder the game expects its files in; on macOS this is the application bundle.</summary>
    public string? InstallDirectory { get; init; }

    public IReadOnlyList<ResolvedFile> Files { get; init; } = [];

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public long TotalSize => Files.Sum(file => file.Size);

    public long TotalDownloadSize => Files.Sum(file => file.DownloadSize);

    public int TotalChunks => Files.Sum(file => file.Chunks.Count);
}

public sealed record ResolvedFile
{
    /// <summary>Validated relative path using this platform's separator.</summary>
    public required string Path { get; init; }

    /// <summary>Product this file's chunks belong to — DLC depots use a different id to the base game.</summary>
    public required long ProductId { get; init; }

    public string? Sha256 { get; init; }

    public IReadOnlyList<string> Flags { get; init; } = [];

    public IReadOnlyList<ResolvedChunk> Chunks { get; init; } = [];

    public long Size => Chunks.Sum(chunk => chunk.Size);

    public long DownloadSize => Chunks.Sum(chunk => chunk.CompressedSize);

    public bool IsExecutable => Flags.Contains("executable", StringComparer.OrdinalIgnoreCase);
}

public sealed record ResolvedChunk
{
    public required string CompressedMd5 { get; init; }

    public string? Md5 { get; init; }

    public long Size { get; init; }

    public long CompressedSize { get; init; }

    /// <summary>Byte offset of this chunk within its file.</summary>
    public long Offset { get; init; }
}

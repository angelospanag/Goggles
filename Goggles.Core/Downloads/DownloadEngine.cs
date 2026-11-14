using System.Diagnostics;
using System.Security.Cryptography;
using Goggles.Core.Api.ContentSystem;
using Goggles.Core.Manifests;

namespace Goggles.Core.Downloads;

/// <summary>
/// Fetches a resolved build to disk: chunks in parallel, verified on both sides of decompression,
/// written straight to their offset in the target file, and resumable.
/// </summary>
/// <remarks>
/// Wants an <see cref="HttpClient"/> without <c>GogAuthHandler</c> — chunk URLs carry their own
/// signature and the CDN has no use for a bearer token.
/// </remarks>
public sealed class DownloadEngine
{
    private readonly HttpClient _http;
    private readonly SecureLinkProvider _links;
    private readonly DownloadOptions _options;

    public DownloadEngine(HttpClient http, SecureLinkProvider links, DownloadOptions? options = null)
    {
        _http = http;
        _links = links;
        _options = options ?? new DownloadOptions();
    }

    /// <param name="build">The build to fetch, already resolved to files and chunks.</param>
    /// <param name="destinationDirectory">Where the game's files go. Created if missing.</param>
    /// <param name="progress">Optional progress sink, called as each chunk settles.</param>
    /// <param name="cancellationToken">Cancels the download; partial files are left for resume.</param>
    public async Task<DownloadReport> DownloadAsync(
        ResolvedBuild build,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(destinationDirectory);

        // Which files were already on disk decides whether resume has anything to check: a file we
        // create ourselves is all zeroes, and hashing it would be wasted work.
        var resumable = _options.Resume ? ExistingFiles(build, destinationDirectory) : [];
        PrepareFiles(build, destinationDirectory);

        var state = new ProgressState(build.TotalSize, build.TotalChunks);
        var work = build.Files.SelectMany(file => file.Chunks.Select(chunk => (File: file, Chunk: chunk)));

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelChunks),
                CancellationToken = cancellationToken,
            },
            async (item, token) =>
            {
                var path = Path.Combine(destinationDirectory, item.File.Path);
                var skipped = resumable.Contains(item.File.Path) &&
                              await ChunkIsIntactAsync(path, item.Chunk, token).ConfigureAwait(false);

                if (!skipped)
                {
                    await FetchChunkAsync(item.File, item.Chunk, path, token).ConfigureAwait(false);
                }

                // Advanced first and reported second: `progress?.Report(state.Advance(…))` would
                // skip the whole expression, counters included, whenever no sink is attached.
                var snapshot = state.Advance(item.Chunk.Size, skipped, item.File.Path);
                progress?.Report(snapshot);
            }).ConfigureAwait(false);

        if (_options.VerifyFileHashes)
        {
            await VerifyFilesAsync(build, destinationDirectory, cancellationToken).ConfigureAwait(false);
        }

        ApplyExecutableFlags(build, destinationDirectory);
        stopwatch.Stop();

        return new DownloadReport
        {
            FilesWritten = build.Files.Count,
            ChunksDownloaded = state.Downloaded,
            ChunksSkipped = state.Skipped,
            BytesWritten = state.BytesCompleted - state.BytesSkipped,
            Duration = stopwatch.Elapsed,
        };
    }

    private static HashSet<string> ExistingFiles(ResolvedBuild build, string destination) =>
        build.Files
            .Where(file => File.Exists(Path.Combine(destination, file.Path)))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates directories and sizes each file up front, so writes never have to extend one.</summary>
    private static void PrepareFiles(ResolvedBuild build, string destination)
    {
        foreach (var file in build.Files)
        {
            var path = Path.Combine(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write);

            // Never shrink: a longer file is a partial download to be resumed, not a mistake.
            if (RandomAccess.GetLength(handle) < file.Size)
            {
                RandomAccess.SetLength(handle, file.Size);
            }
        }
    }

    private async Task FetchChunkAsync(
        ResolvedFile file,
        ResolvedChunk chunk,
        string path,
        CancellationToken cancellationToken)
    {
        var endpoints = await _links.GetEndpointsAsync(file.ProductId, cancellationToken).ConfigureAwait(false);
        var attempts = Math.Max(1, _options.MaxAttemptsPerChunk);
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            // Rotate endpoints across attempts so a sick CDN node does not fail the whole download.
            var endpoint = endpoints[attempt % endpoints.Count];

            try
            {
                var compressed = await _http
                    .GetByteArrayAsync(endpoint.BuildChunkUri(chunk.CompressedMd5), cancellationToken)
                    .ConfigureAwait(false);

                var plain = Verify(chunk, compressed);

                using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write);
                await RandomAccess.WriteAsync(handle, plain, chunk.Offset, cancellationToken)
                    .ConfigureAwait(false);

                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
            {
                lastFailure = exception;

                // A rejected signature usually means the token aged out mid-download.
                if (exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden })
                {
                    _links.Invalidate(file.ProductId);
                    endpoints = await _links.GetEndpointsAsync(file.ProductId, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (attempt < attempts - 1)
                {
                    await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidDataException(
            $"Gave up on chunk {chunk.CompressedMd5} of '{file.Path}' after {attempts} attempts: " +
            $"{lastFailure?.Message}",
            lastFailure);
    }

    /// <summary>Checks a chunk on both sides of decompression before a single byte reaches disk.</summary>
    private static byte[] Verify(ResolvedChunk chunk, byte[] compressed)
    {
        var compressedMd5 = Convert.ToHexStringLower(MD5.HashData(compressed));
        if (!compressedMd5.Equals(chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Chunk hash mismatch: expected {chunk.CompressedMd5}, got {compressedMd5}.");
        }

        var plain = CdnClient.DecompressToBytes(compressed);

        if (plain.LongLength != chunk.Size)
        {
            throw new InvalidDataException(
                $"Chunk {chunk.CompressedMd5} inflated to {plain.LongLength} bytes, expected {chunk.Size}.");
        }

        if (chunk.Md5 is { Length: > 0 } expected)
        {
            var actual = Convert.ToHexStringLower(MD5.HashData(plain));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Inflated chunk hash mismatch: expected {expected}, got {actual}.");
            }
        }

        return plain;
    }

    private static async Task<bool> ChunkIsIntactAsync(
        string path,
        ResolvedChunk chunk,
        CancellationToken cancellationToken)
    {
        if (chunk.Md5 is not { Length: > 0 } expected)
        {
            return false;
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

            if (RandomAccess.GetLength(handle) < chunk.Offset + chunk.Size)
            {
                return false;
            }

            var buffer = new byte[chunk.Size];
            var read = await RandomAccess
                .ReadAsync(handle, buffer, chunk.Offset, cancellationToken)
                .ConfigureAwait(false);

            return read == buffer.Length &&
                   Convert.ToHexStringLower(MD5.HashData(buffer))
                       .Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // Unreadable means "download it again", not "fail the whole build".
            return false;
        }
    }

    private static async Task VerifyFilesAsync(
        ResolvedBuild build,
        string destination,
        CancellationToken cancellationToken)
    {
        foreach (var file in build.Files.Where(file => file.Sha256 is { Length: > 0 }))
        {
            var path = Path.Combine(destination, file.Path);

            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"'{file.Path}' failed verification: expected {file.Sha256}, got {actual}.");
            }
        }
    }

    private static void ApplyExecutableFlags(ResolvedBuild build, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var file in build.Files.Where(file => file.IsExecutable))
        {
            var path = Path.Combine(destination, file.Path);
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Progress counters shared across the parallel chunk workers.</summary>
    private sealed class ProgressState(long totalBytes, int totalChunks)
    {
        private readonly Lock _gate = new();

        public long BytesCompleted { get; private set; }

        public long BytesSkipped { get; private set; }

        public int Downloaded { get; private set; }

        public int Skipped { get; private set; }

        public DownloadProgress Advance(long bytes, bool skipped, string path)
        {
            lock (_gate)
            {
                BytesCompleted += bytes;

                if (skipped)
                {
                    Skipped++;
                    BytesSkipped += bytes;
                }
                else
                {
                    Downloaded++;
                }

                return new DownloadProgress
                {
                    BytesCompleted = BytesCompleted,
                    TotalBytes = totalBytes,
                    ChunksCompleted = Downloaded + Skipped,
                    TotalChunks = totalChunks,
                    BytesSkipped = BytesSkipped,
                    CurrentPath = path,
                };
            }
        }
    }
}

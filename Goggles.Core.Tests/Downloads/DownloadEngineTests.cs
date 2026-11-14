using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Goggles.Core.Api.ContentSystem;
using Goggles.Core.Downloads;
using Goggles.Core.Manifests;

namespace Goggles.Core.Tests.Downloads;

public class DownloadEngineTests : IDisposable
{
    private const int ChunkSize = 64;

    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-download").FullName;
    private readonly Dictionary<string, byte[]> _cdn = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Writes_every_chunk_to_its_offset()
    {
        var content = Content(200);
        var build = BuildFor(("data/game.bin", content));

        var report = await CreateEngine(out _).DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(
            Path.Combine(_directory, "data", "game.bin"),
            TestContext.Current.CancellationToken));
        Assert.Equal(4, report.ChunksDownloaded);
        Assert.Equal(0, report.ChunksSkipped);
        Assert.Equal(200, report.BytesWritten);
    }

    [Fact]
    public async Task Writes_several_files()
    {
        var first = Content(100);
        var second = Content(30);
        var build = BuildFor(("a.bin", first), ("nested/b.bin", second));

        await CreateEngine(out _).DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first, await File.ReadAllBytesAsync(
            Path.Combine(_directory, "a.bin"),
            TestContext.Current.CancellationToken));
        Assert.Equal(second, await File.ReadAllBytesAsync(
            Path.Combine(_directory, "nested", "b.bin"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reports_progress_for_every_chunk()
    {
        var build = BuildFor(("game.bin", Content(200)));
        var reports = new List<DownloadProgress>();

        await CreateEngine(out _).DownloadAsync(
            build,
            _directory,
            new Progress<DownloadProgress>(report =>
            {
                lock (reports)
                {
                    reports.Add(report);
                }
            }),
            TestContext.Current.CancellationToken);

        // Progress is raised asynchronously, so wait for the last one rather than racing it.
        await WaitUntil(() => reports.Count == 4);
        Assert.Equal(200, reports.Max(report => report.BytesCompleted));
        Assert.Equal(1, reports.Max(report => report.Fraction));
    }

    [Fact]
    public async Task Resuming_an_intact_install_downloads_nothing()
    {
        var build = BuildFor(("game.bin", Content(200)));
        var engine = CreateEngine(out var transport);

        await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);
        var downloadsAfterFirstRun = transport.CallCount;

        var report = await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.ChunksDownloaded);
        Assert.Equal(4, report.ChunksSkipped);
        Assert.Equal(0, report.BytesWritten);
        Assert.Equal(downloadsAfterFirstRun, transport.CallCount);
    }

    [Fact]
    public async Task Resuming_refetches_only_the_damaged_chunk()
    {
        var content = Content(200);
        var build = BuildFor(("game.bin", content));
        var engine = CreateEngine(out _);
        var path = Path.Combine(_directory, "game.bin");

        await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        // Corrupt the third chunk.
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write))
        {
            RandomAccess.Write(handle, new byte[ChunkSize], ChunkSize * 2);
        }

        var report = await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ChunksDownloaded);
        Assert.Equal(3, report.ChunksSkipped);
        Assert.Equal(content, await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_truncated_file_is_finished_rather_than_restarted()
    {
        var content = Content(200);
        var build = BuildFor(("game.bin", content));
        var engine = CreateEngine(out _);
        var path = Path.Combine(_directory, "game.bin");

        await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(ChunkSize * 2);
        }

        var report = await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ChunksDownloaded);
        Assert.Equal(content, await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retries_a_failing_chunk_and_recovers()
    {
        var content = Content(64);
        var build = BuildFor(("game.bin", content));
        var failures = 0;

        var engine = CreateEngine(
            out _,
            onChunkRequest: () => Interlocked.Increment(ref failures) == 1
                ? HttpStatusCode.InternalServerError
                : null);

        var report = await engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ChunksDownloaded);
        Assert.Equal(content, await File.ReadAllBytesAsync(
            Path.Combine(_directory, "game.bin"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gives_up_after_the_attempt_limit()
    {
        var build = BuildFor(("game.bin", Content(64)));
        var engine = CreateEngine(out _, onChunkRequest: () => HttpStatusCode.InternalServerError);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.DownloadAsync(
                build,
                _directory,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Gave up on chunk", exception.Message);
    }

    [Fact]
    public async Task A_chunk_whose_bytes_do_not_match_is_rejected()
    {
        var build = BuildFor(("game.bin", Content(64)));

        // Serve something plausible but wrong for every chunk.
        foreach (var key in _cdn.Keys.ToList())
        {
            _cdn[key] = Compress(Content(64));
        }

        var engine = CreateEngine(out _);

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verifies_file_hashes_when_asked()
    {
        var content = Content(100);
        var build = BuildFor(("game.bin", content)) with { };
        var wrongHash = build.Files[0] with { Sha256 = new string('0', 64) };
        build = build with { Files = [wrongHash] };

        var engine = CreateEngine(out _, new DownloadOptions { VerifyFileHashes = true });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.DownloadAsync(
                build,
                _directory,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("failed verification", exception.Message);
    }

    [Fact]
    public async Task Marks_executables_executable_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var build = BuildFor(("game.bin", Content(64)));
        build = build with { Files = [build.Files[0] with { Flags = ["executable"] }] };

        await CreateEngine(out _).DownloadAsync(
            build,
            _directory,
            cancellationToken: TestContext.Current.CancellationToken);

        var mode = File.GetUnixFileMode(Path.Combine(_directory, "game.bin"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Cancellation_stops_the_download()
    {
        using var cancellation = new CancellationTokenSource();
        var build = BuildFor(("game.bin", Content(640)));

        var engine = CreateEngine(out _, onChunkRequest: () =>
        {
            cancellation.Cancel();
            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.DownloadAsync(build, _directory, cancellationToken: cancellation.Token));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        Random.Shared.NextBytes(content);

        return content;
    }

    /// <summary>Splits each file into chunks, registers them with the fake CDN, and describes them.</summary>
    private ResolvedBuild BuildFor(params (string Path, byte[] Content)[] files)
    {
        var resolved = new List<ResolvedFile>();

        foreach (var (path, content) in files)
        {
            var chunks = new List<ResolvedChunk>();

            for (var offset = 0; offset < content.Length; offset += ChunkSize)
            {
                var slice = content[offset..Math.Min(offset + ChunkSize, content.Length)];
                var compressed = Compress(slice);
                var compressedMd5 = Hash(compressed);

                _cdn[compressedMd5] = compressed;
                chunks.Add(new ResolvedChunk
                {
                    CompressedMd5 = compressedMd5,
                    Md5 = Hash(slice),
                    Size = slice.Length,
                    CompressedSize = compressed.Length,
                    Offset = offset,
                });
            }

            resolved.Add(new ResolvedFile
            {
                Path = ManifestResolver.ToRelativePath(path),
                ProductId = 1,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                Chunks = chunks,
            });
        }

        return new ResolvedBuild { BuildId = "b1", Platform = "windows", Files = resolved };
    }

    private DownloadEngine CreateEngine(
        out StubHttpMessageHandler transport,
        DownloadOptions? options = null,
        Func<HttpStatusCode?>? onChunkRequest = null)
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!;

            if (uri.Host == "content-system.gog.com")
            {
                return StubHttpMessageHandler.Json(SecureLinkJson);
            }

            if (onChunkRequest?.Invoke() is { } failure)
            {
                return StubHttpMessageHandler.Json("nope", failure);
            }

            var hash = uri.Segments[^1];

            return _cdn.TryGetValue(hash, out var chunk)
                ? StubHttpMessageHandler.Bytes(chunk)
                : StubHttpMessageHandler.Json("missing", HttpStatusCode.NotFound);
        });

        transport = handler;
        var client = new HttpClient(handler);

        return new DownloadEngine(
            client,
            new SecureLinkProvider(new SecureLinkClient(client)),
            options ?? new DownloadOptions { RetryDelay = TimeSpan.Zero });
    }

    private static byte[] Compress(byte[] content)
    {
        using var buffer = new MemoryStream();

        using (var compressor = new ZLibStream(buffer, CompressionLevel.Optimal))
        {
            compressor.Write(content);
        }

        return buffer.ToArray();
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(MD5.HashData(content));

    private const string SecureLinkJson = """
        {
          "product_id": 1,
          "type": "depot",
          "urls": [
            {
              "endpoint_name": "test",
              "url_format": "{base_url}/{path}",
              "parameters": { "base_url": "https://cdn.test", "path": "store" },
              "priority": 10,
              "supports_generation": [2],
              "fallback_only": false
            }
          ]
        }
        """;
}

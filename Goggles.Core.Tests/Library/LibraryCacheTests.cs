using Goggles.Core.Library;

namespace Goggles.Core.Tests.Library;

public class LibraryCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-library-cache-").FullName;
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string CachePath => Path.Combine(_directory, "library.json");

    private LibraryCache CreateCache() => new(CachePath, _time);

    private static LibrarySyncResult Synced(string checksum = "abc123") => new()
    {
        Changed = true,
        Checksum = checksum,
        AccountName = "angelos",
        Games =
        [
            new OwnedGame
            {
                Id = 1136126792,
                Title = "Absolute Drift",
                ImageBaseUrl = new Uri("https://images.gog-statics.com/abc"),
                ReleasedAt = new DateTimeOffset(2016, 7, 21, 0, 0, 0, TimeSpan.Zero),
                Platforms = GamePlatforms.Windows | GamePlatforms.Mac,
                TagIds = ["12"],
                CommunityRating = 4,
            },
            new OwnedGame { Id = 2075976504, Title = "A.D. 2044" },
        ],
    };

    [Fact]
    public async Task A_saved_library_loads_back_for_the_same_account()
    {
        var cache = CreateCache();
        await cache.SaveAsync("42", Synced(), TestContext.Current.CancellationToken);

        var loaded = await cache.LoadAsync("42", TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded.Checksum);
        Assert.Equal("angelos", loaded.AccountName);
        Assert.Equal(Now, loaded.SavedAt);
        Assert.Equal(Synced().Games, loaded.Games, (a, b) => a with { TagIds = [] } == b with { TagIds = [] }
                                                             && a.TagIds.SequenceEqual(b.TagIds));
    }

    [Fact]
    public async Task Another_accounts_library_is_never_returned()
    {
        var cache = CreateCache();
        await cache.SaveAsync("42", Synced(), TestContext.Current.CancellationToken);

        Assert.Null(await cache.LoadAsync("43", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nothing_cached_reads_as_null()
    {
        Assert.Null(await CreateCache().LoadAsync("42", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"version":999,"userId":"42","games":[]}""")]
    [InlineData("""{"userId":"42","games":[]}""")]
    public async Task A_corrupt_or_foreign_format_file_reads_as_null(string contents)
    {
        await File.WriteAllTextAsync(CachePath, contents, TestContext.Current.CancellationToken);

        Assert.Null(await CreateCache().LoadAsync("42", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unchanged_sync_leaves_the_file_alone()
    {
        var cache = CreateCache();

        await cache.SaveAsync("42", LibrarySyncResult.Unchanged("abc123"), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Saving_leaves_no_temporary_file_behind()
    {
        await CreateCache().SaveAsync("42", Synced(), TestContext.Current.CancellationToken);

        Assert.Equal([CachePath], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task The_checksum_is_trusted_only_while_the_cache_is_young()
    {
        var cache = CreateCache();
        await cache.SaveAsync("42", Synced(), TestContext.Current.CancellationToken);
        var loaded = await cache.LoadAsync("42", TestContext.Current.CancellationToken);

        _time.Advance(LibraryCache.MaxAge - TimeSpan.FromMinutes(1));
        Assert.Equal("abc123", cache.ChecksumWorthTrusting(loaded));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(cache.ChecksumWorthTrusting(loaded));
    }

    [Fact]
    public void No_cache_means_no_checksum()
    {
        Assert.Null(CreateCache().ChecksumWorthTrusting(null));
    }

    [Fact]
    public async Task Clearing_removes_the_file_and_tolerates_there_being_none()
    {
        var cache = CreateCache();
        await cache.SaveAsync("42", Synced(), TestContext.Current.CancellationToken);

        cache.Clear();
        cache.Clear();

        Assert.False(File.Exists(CachePath));
        Assert.Null(await cache.LoadAsync("42", TestContext.Current.CancellationToken));
    }
}

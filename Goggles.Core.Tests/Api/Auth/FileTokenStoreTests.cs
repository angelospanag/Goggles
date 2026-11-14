using Goggles.Core.Api.Auth;

namespace Goggles.Core.Tests.Api.Auth;

public class FileTokenStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-tests").FullName;

    private static readonly TokenSet Tokens = new()
    {
        AccessToken = "access-1",
        RefreshToken = "refresh-1",
        ExpiresAt = new DateTimeOffset(2026, 8, 12, 11, 0, 0, TimeSpan.Zero),
        UserId = "48485266454929432",
        SessionId = "session-1",
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Round_trips_a_token_set()
    {
        var store = CreateStore();

        await store.SaveAsync(Tokens, TestContext.Current.CancellationToken);

        Assert.Equal(Tokens, await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Loads_null_when_nothing_has_been_saved()
    {
        Assert.Null(await CreateStore().LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creates_missing_directories()
    {
        var store = new FileTokenStore(Path.Combine(_directory, "nested", "deeper", "tokens.json"));

        await store.SaveAsync(Tokens, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(store.Path));
    }

    [Fact]
    public async Task Overwrites_an_earlier_token_set_and_leaves_no_temp_file()
    {
        var store = CreateStore();
        await store.SaveAsync(Tokens, TestContext.Current.CancellationToken);

        var replacement = Tokens with { AccessToken = "access-2" };
        await store.SaveAsync(replacement, TestContext.Current.CancellationToken);

        Assert.Equal(replacement, await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public async Task Treats_a_corrupt_file_as_signed_out()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(
            store.Path,
            "{ this is not json",
            TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clearing_is_safe_when_there_is_nothing_to_clear()
    {
        var store = CreateStore();

        await store.ClearAsync(TestContext.Current.CancellationToken);
        await store.SaveAsync(Tokens, TestContext.Current.CancellationToken);
        await store.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writes_the_file_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = CreateStore();
        await store.SaveAsync(Tokens, TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
    }

    private FileTokenStore CreateStore() => new(Path.Combine(_directory, "tokens.json"));
}

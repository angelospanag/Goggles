using System.Text.Json;

namespace Goggles.Core.Api.Auth;

/// <summary>
/// Stores credentials as JSON under the user's application data directory, with owner-only
/// permissions on Unix.
/// </summary>
/// <remarks>
/// This is plaintext on disk. It is deliberately the first implementation — OS-backed secure
/// storage (Keychain, DPAPI, libsecret) can slot in behind <see cref="ITokenStore"/> later without
/// touching callers.
/// </remarks>
public sealed class FileTokenStore : ITokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileTokenStore(string? path = null) => Path = path ?? DefaultPath();

    public string Path { get; }

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "Goggles",
        "tokens.json");

    public async ValueTask<TokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            await using var stream = File.OpenRead(Path);
            return await JsonSerializer
                .DeserializeAsync<TokenSet>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A corrupt store is indistinguishable from being signed out; the user can sign in again.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // Write beside the target and swap, so a crash mid-write cannot leave a half file.
            var temporary = Path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                SetOwnerOnly(temporary);
                await JsonSerializer
                    .SerializeAsync(stream, tokens, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(Path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

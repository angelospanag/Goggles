using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace Goggles.Desktop.Services;

/// <summary>
/// Fetches library cover art, keeping a copy on disk so a second launch does not re-download
/// hundreds of thumbnails.
/// </summary>
public sealed class CoverImageCache : IDisposable
{
    /// <summary>
    /// GOG image URLs are extension-less; the suffix picks a rendition. <c>_196</c> is the small
    /// tile the store itself uses — about 7 KB against 200 KB for the full-size image.
    /// </summary>
    private const string TileSuffix = "_196.jpg";

    private const int TileDecodeWidth = 196;

    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _throttle = new(6);
    private readonly string? _directory;

    public CoverImageCache(string? directory = null)
    {
        var path = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "Goggles",
            "covers");

        try
        {
            Directory.CreateDirectory(path);
            _directory = path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only or sandboxed home directory costs us the cache, not the covers: fall back
            // to fetching every time rather than failing application start-up.
            _directory = null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }

    /// <summary>
    /// Returns the cover for a library image URL, or null if it cannot be fetched — a missing
    /// thumbnail is a cosmetic problem, never a reason to fail a library load.
    /// </summary>
    public Task<Bitmap?> LoadAsync(Uri? imageBaseUrl, CancellationToken cancellationToken = default) =>
        imageBaseUrl is null ? Task.FromResult<Bitmap?>(null)
        : FetchAsync(new Uri(imageBaseUrl + TileSuffix), TileDecodeWidth, cancellationToken);

    /// <summary>
    /// Returns the image at <paramref name="fullUrl"/> — already a complete URL, unlike
    /// <see cref="LoadAsync"/>'s tile art, which needs a size suffix appended. Used for the
    /// per-game detail page's hero background and for achievement icons, decoded at native
    /// resolution rather than downscaled to tile size. Returns null if it cannot be fetched —
    /// same cosmetic-not-fatal contract as <see cref="LoadAsync"/>.
    /// </summary>
    public Task<Bitmap?> LoadRawAsync(Uri? fullUrl, CancellationToken cancellationToken = default) =>
        fullUrl is null ? Task.FromResult<Bitmap?>(null) : FetchAsync(fullUrl, decodeWidth: null, cancellationToken);

    private async Task<Bitmap?> FetchAsync(Uri uri, int? decodeWidth, CancellationToken cancellationToken)
    {
        var path = _directory is null ? null : Path.Combine(_directory, FileNameFor(uri));

        try
        {
            if (path is not null && File.Exists(path))
            {
                return Decode(path, decodeWidth);
            }

            await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Another caller may have fetched the same image while this one queued.
                if (path is not null && File.Exists(path))
                {
                    return Decode(path, decodeWidth);
                }

                var bytes = await _http.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);

                if (path is null)
                {
                    using var memory = new MemoryStream(bytes);
                    return decodeWidth is int width ? Bitmap.DecodeToWidth(memory, width) : new Bitmap(memory);
                }

                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _throttle.Release();
            }

            return Decode(path, decodeWidth);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static Bitmap? Decode(string path, int? decodeWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return decodeWidth is int width ? Bitmap.DecodeToWidth(stream, width) : new Bitmap(stream);
        }
        catch (Exception)
        {
            // A truncated or corrupt file: drop it so the next launch fetches it again.
            File.Delete(path);
            return null;
        }
    }

    private static string FileNameFor(Uri uri)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(uri.ToString()));

        return Convert.ToHexStringLower(hash) + ".jpg";
    }
}

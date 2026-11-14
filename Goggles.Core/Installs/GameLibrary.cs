using System.Text.Json;
using Goggles.Core.Launching;

namespace Goggles.Core.Installs;

/// <summary>
/// Where installed games live, and what is already there.
/// </summary>
/// <remarks>
/// A build goes into the library root under the directory name GOG's manifest gives it — nothing is
/// wrapped or renamed:
/// <code>
/// ~/Games/Goggles/Tyranny/…
/// ~/Games/Goggles/absolutedrift.app/…
/// </code>
/// A game counts as installed when the <c>goggame-{productId}.info</c> marker GOG ships inside the
/// build is present, which is what makes the installed filter a single directory listing rather
/// than a manifest fetch per game.
/// </remarks>
public sealed class GameLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public GameLibrary(string root) => Root = root;

    public string Root { get; }

    /// <summary>Where a build's files go: the manifest's own directory, under the library root.</summary>
    public string InstallPathFor(long productId, string? installDirectory) =>
        Path.Combine(Root, string.IsNullOrWhiteSpace(installDirectory)
            ? productId.ToString()
            : installDirectory);

    /// <summary>Every install found under the root.</summary>
    public IReadOnlyList<InstalledGame> Scan()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(Root)
            .Select(Read)
            .OfType<InstalledGame>()
            .ToList();
    }

    public InstalledGame? Find(long productId) =>
        Scan().FirstOrDefault(installed => installed.ProductId == productId);

    /// <summary>Whether a directory holds an install of the given product.</summary>
    public static bool Contains(string installDirectory, long productId) =>
        GameLauncher.FindInfoFile(installDirectory, productId) is not null;

    private static InstalledGame? Read(string installDirectory)
    {
        // The marker sits at the install root, or in Contents/Resources for a macOS bundle.
        var marker = MarkerIn(installDirectory) ??
                     MarkerIn(Path.Combine(installDirectory, "Contents", "Resources"));

        if (marker is null)
        {
            return null;
        }

        try
        {
            var info = JsonSerializer.Deserialize<GameInfo>(File.ReadAllText(marker), JsonOptions);

            if (!long.TryParse(info?.GameId, out var productId))
            {
                return null;
            }

            return new InstalledGame
            {
                ProductId = productId,
                InstallPath = installDirectory,
                Name = info?.Name,
                BuildId = info?.BuildId,
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // An unreadable marker means "not installed", not "crash the scan".
            return null;
        }
    }

    private static string? MarkerIn(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "goggame-*.info").FirstOrDefault()
        : null;
}

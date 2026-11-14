namespace Goggles.Core.Installs;

/// <summary>
/// Removes an installed game.
/// </summary>
/// <remarks>
/// This deletes a directory tree, so every call is checked against three things before anything is
/// removed: the target must sit inside the library root, must not be the root itself, and must
/// contain the <c>goggame-{productId}.info</c> file that marks it as the install Goggles thinks it
/// is. A wrong path fails loudly rather than deleting something else.
/// </remarks>
public sealed class GameUninstaller
{
    /// <summary>Bytes an install occupies, or 0 if it is not there.</summary>
    public static long Measure(string installDirectory)
    {
        if (!Directory.Exists(installDirectory))
        {
            return 0;
        }

        return new DirectoryInfo(installDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length);
    }

    /// <summary>
    /// Deletes an install.
    /// </summary>
    /// <param name="libraryRoot">The directory games are installed under.</param>
    /// <param name="installDirectory">The install to remove; must be inside the library root.</param>
    /// <param name="productId">Used to confirm the directory really is this game's install.</param>
    /// <exception cref="InvalidOperationException">The target failed one of the safety checks.</exception>
    public UninstallResult Uninstall(string libraryRoot, string installDirectory, long productId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));

        if (target == root)
        {
            throw new InvalidOperationException(
                $"Refusing to delete the library root itself ({root}).");
        }

        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{target}' is outside the library root ({root}); refusing to delete it.");
        }

        if (!Directory.Exists(target))
        {
            return new UninstallResult { Removed = false, BytesFreed = 0, Path = target };
        }

        // Deleting through a symlink would remove whatever it points at.
        if (File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"'{target}' is a link rather than a real directory; refusing to delete it.");
        }

        if (!GameLibrary.Contains(target, productId))
        {
            throw new InvalidOperationException(
                $"'{target}' has no goggame-{productId}.info, so it does not look like this game's " +
                "install; refusing to delete it.");
        }

        var size = Measure(target);
        Directory.Delete(target, recursive: true);

        return new UninstallResult { Removed = true, BytesFreed = size, Path = target };
    }
}

public sealed record UninstallResult
{
    /// <summary>False when there was nothing there to remove.</summary>
    public required bool Removed { get; init; }

    public required long BytesFreed { get; init; }

    public required string Path { get; init; }
}

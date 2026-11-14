namespace Goggles.Core.Launching;

/// <summary>
/// Finds the compatibility layers installed on this machine: CrossOver with each of its bottles,
/// and a plain <c>wine</c>. Nothing is downloaded or installed — this only looks.
/// </summary>
public static class WindowsRunnerDiscovery
{
    /// <summary>Where Goggles keeps its own Wine prefix, for runners that need one.</summary>
    public static string DefaultWinePrefix() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Goggles",
        "WinePrefix");

    /// <summary>The runners found in the standard places, CrossOver bottles first.</summary>
    public static IReadOnlyList<WindowsRunner> Find()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows builds run natively there; there is nothing to find.
            return [];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Find(
            CrossOverRunner.DefaultAppPath,
            Path.Combine(home, "Library", "Application Support", "CrossOver", "Bottles"),
            WineCandidates(),
            DefaultWinePrefix());
    }

    /// <summary>The same search against explicit locations, so it can be tested.</summary>
    public static IReadOnlyList<WindowsRunner> Find(
        string crossOverAppPath,
        string crossOverBottlesDirectory,
        IEnumerable<string> wineCandidates,
        string winePrefix)
    {
        var runners = new List<WindowsRunner>();

        var crossOver = new CrossOverRunner { AppPath = crossOverAppPath, Bottle = string.Empty };
        if (crossOver.IsAvailable && Directory.Exists(crossOverBottlesDirectory))
        {
            // A bottle is a directory holding cxbottle.conf; anything else in there is not one.
            runners.AddRange(Directory
                .EnumerateDirectories(crossOverBottlesDirectory)
                .Where(bottle => File.Exists(Path.Combine(bottle, "cxbottle.conf")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(bottle => crossOver with { Bottle = bottle }));
        }

        if (wineCandidates.FirstOrDefault(File.Exists) is { } wine)
        {
            runners.Add(new WineRunner { WineBinary = wine, Prefix = winePrefix });
        }

        return runners;
    }

    /// <summary>
    /// Every directory on <c>PATH</c>, then the places Homebrew and the macOS Wine app bundles
    /// install to — a GUI app launched from the Dock does not inherit the shell's <c>PATH</c>, so
    /// those are checked explicitly.
    /// </summary>
    private static IEnumerable<string> WineCandidates()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        IEnumerable<string> directories =
        [
            .. path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/Applications/Wine Stable.app/Contents/Resources/wine/bin",
            "/Applications/Wine Devel.app/Contents/Resources/wine/bin",
            "/Applications/Wine Staging.app/Contents/Resources/wine/bin",
            "/usr/bin",
        ];

        return directories
            .Distinct()
            .SelectMany(directory => new[] { Path.Combine(directory, "wine"), Path.Combine(directory, "wine64") });
    }
}

namespace Goggles.Desktop.ViewModels;

/// <summary>How the library grid is ordered.</summary>
public enum LibrarySort
{
    /// <summary>Alphabetical.</summary>
    Title,

    /// <summary>
    /// GOG's own order, which is by purchase date with the newest first — the order the library
    /// arrives in, and the one most launchers open on.
    /// </summary>
    RecentlyAdded,

    /// <summary>Newest games first, by the date the game was published rather than bought.</summary>
    ReleaseDate,
}

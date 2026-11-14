namespace Goggles.Desktop.ViewModels;

/// <summary>Which slice of the library the grid shows.</summary>
public enum LibraryFilter
{
    All,
    Installed,
    NotInstalled,

    /// <summary>
    /// Games with a build for the platform Goggles is running on. Most of a GOG library is
    /// Windows-only, and none of that can be launched until Wine orchestration exists, so this is
    /// the filter that shows what is actually playable.
    /// </summary>
    Playable,
}

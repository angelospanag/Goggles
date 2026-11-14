namespace Goggles.Desktop.ViewModels;

/// <summary>
/// How the library is laid out. Named "Layout" rather than "View" to avoid colliding with
/// <c>Goggles.Desktop.Views.LibraryView</c>, the actual UserControl.
/// </summary>
public enum LibraryLayout
{
    /// <summary>Cover-art tiles.</summary>
    Grid,

    /// <summary>Rows with trophy %, playtime, rating, and platform columns.</summary>
    List,
}

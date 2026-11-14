namespace Goggles.Core.Library;

/// <summary>The platforms a product ships builds for.</summary>
[Flags]
public enum GamePlatforms
{
    None = 0,
    Windows = 1,
    Mac = 2,
    Linux = 4,
}

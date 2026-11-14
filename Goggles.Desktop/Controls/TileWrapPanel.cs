using Avalonia;
using Avalonia.Controls;

namespace Goggles.Desktop.Controls;

/// <summary>
/// Lays tiles out in rows of equal-width columns that always span the full width: as many columns as
/// <see cref="MinItemWidth"/> allows, each then stretched to share the leftover space.
/// </summary>
/// <remarks>
/// A <see cref="WrapPanel"/> keeps every tile at its fixed width and leaves the remainder of each row
/// empty, so the library grid had a gap on the right of up to a whole tile that changed size as the
/// window did. Like WrapPanel, this does not virtualise.
/// </remarks>
public sealed class TileWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<TileWrapPanel, double>(nameof(MinItemWidth), 200);

    static TileWrapPanel() => AffectsMeasure<TileWrapPanel>(MinItemWidthProperty);

    /// <summary>The narrowest a tile may be, margins included; tiles are never narrower.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var tiles = VisibleTiles();
        if (tiles.Count == 0)
        {
            return default;
        }

        // Unbounded width (nothing constrains the panel yet): fall back to one row of minimum-width
        // tiles, which is what a WrapPanel would ask for too.
        var unbounded = double.IsInfinity(availableSize.Width);
        var columns = unbounded ? tiles.Count : ColumnsFor(availableSize.Width);
        var itemWidth = unbounded ? MinItemWidth : availableSize.Width / columns;

        var height = 0.0;
        foreach (var row in tiles.Chunk(columns))
        {
            var rowHeight = 0.0;
            foreach (var tile in row)
            {
                tile.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, tile.DesiredSize.Height);
            }

            height += rowHeight;
        }

        return new Size(unbounded ? itemWidth * columns : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tiles = VisibleTiles();
        if (tiles.Count == 0)
        {
            return finalSize;
        }

        var columns = ColumnsFor(finalSize.Width);
        var itemWidth = finalSize.Width / columns;

        var y = 0.0;
        foreach (var row in tiles.Chunk(columns))
        {
            var rowHeight = row.Max(tile => tile.DesiredSize.Height);
            for (var column = 0; column < row.Length; column++)
            {
                row[column].Arrange(new Rect(column * itemWidth, y, itemWidth, rowHeight));
            }

            y += rowHeight;
        }

        return finalSize;
    }

    /// <summary>Whole columns that fit at <see cref="MinItemWidth"/>; always at least one.</summary>
    private int ColumnsFor(double width) =>
        Math.Max(1, (int)Math.Floor(width / Math.Max(1, MinItemWidth)));

    private List<Control> VisibleTiles() => [.. Children.Where(child => child.IsVisible)];
}

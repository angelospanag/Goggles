using Avalonia;
using Avalonia.Controls;

namespace Goggles.Desktop.Controls;

/// <summary>
/// Takes whatever width it is given and makes its height a fixed proportion of it, so content keeps
/// its shape as the space around it grows — a cover tile that widens grows taller with it instead of
/// cropping more of the art. Avalonia has no built-in aspect-ratio layout.
/// </summary>
public sealed class AspectRatioDecorator : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectRatioDecorator, double>(nameof(Ratio), 1);

    static AspectRatioDecorator() => AffectsMeasure<AspectRatioDecorator>(RatioProperty);

    /// <summary>Height divided by width.</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // With no width to go on, measure the content and keep its width: there is nothing to scale from.
        if (double.IsInfinity(availableSize.Width))
        {
            Child?.Measure(availableSize);
            var natural = Child?.DesiredSize.Width ?? 0;
            return new Size(natural, natural * Ratio);
        }

        var size = new Size(availableSize.Width, availableSize.Width * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

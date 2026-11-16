using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// Draws the last minute of one telemetry series as a line with a soft wash under it.
/// </summary>
/// <remarks>
/// <para>
/// Read-only and deliberately small: no axes, no labels, no tooltips. The number it belongs to is printed next to it in
/// a larger type, so the chart's only job is to answer "is this rising?" at a glance — which a grid and a legend would
/// get in the way of.
/// </para>
/// <para>
/// The colours are styled properties rather than hard-coded brushes, so the view binds them to the palette with
/// <c>DynamicResource</c> and a theme change repaints the chart along with everything else. The control itself knows
/// nothing about themes.
/// </para>
/// <para>
/// The vertical range is the data's own, padded, unless <see cref="Minimum"/> and <see cref="Maximum"/> are both set.
/// A fixed range is what makes two charts comparable; the data's own range is what makes a 3 °C drift visible instead
/// of a flat line at the bottom of a 0–100 scale.
/// </para>
/// </remarks>
public sealed class SparklineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<SparklineChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SparklineChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SparklineChart, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<SparklineChart, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SparklineChart, double>(nameof(Maximum));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SparklineChart, double>(nameof(StrokeThickness), 1.6);

    static SparklineChart()
    {
        AffectsRender<SparklineChart>(
            ValuesProperty, StrokeProperty, FillProperty, MinimumProperty, MaximumProperty, StrokeThicknessProperty);
    }

    /// <summary>The samples to draw, oldest first. Fewer than two draws nothing.</summary>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>The line colour. The view binds this to the palette's accent.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The wash under the line. Null draws the line only.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The bottom of a fixed range. Ignored unless it is below <see cref="Maximum"/>.</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>The top of a fixed range. Ignored unless it is above <see cref="Minimum"/>.</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        var values = Values;
        if (values == null || values.Count < 2) return;

        var (low, high) = Range(values);
        if (high - low < 0.0001) return;

        // A one-pixel inset keeps the stroke from being clipped in half at the top and bottom edges.
        double inset = Math.Max(1, StrokeThickness / 2);
        double plotHeight = Math.Max(1, height - inset * 2);

        var points = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            double x = values.Count == 1 ? 0 : i / (double)(values.Count - 1) * width;
            double ratio = Math.Clamp((values[i] - low) / (high - low), 0, 1);
            points[i] = new Point(x, inset + (1 - ratio) * plotHeight);
        }

        var fill = Fill;
        if (fill != null)
        {
            var area = new StreamGeometry();
            using (var sink = area.Open())
            {
                sink.BeginFigure(new Point(points[0].X, height), true);
                foreach (var point in points) sink.LineTo(point);
                sink.LineTo(new Point(points[^1].X, height));
                sink.EndFigure(true);
            }

            context.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var sink = line.Open())
        {
            sink.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++) sink.LineTo(points[i]);
            sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Stroke ?? Brushes.White, StrokeThickness), line);
    }

    /// <summary>
    /// The vertical range to draw in. A fixed range wins when both ends are set; otherwise the data's own range is
    /// padded by a tenth so a flat series does not sit exactly on an edge.
    /// </summary>
    private (double Low, double High) Range(IReadOnlyList<double> values)
    {
        if (Maximum > Minimum) return (Minimum, Maximum);

        double low = double.MaxValue;
        double high = double.MinValue;
        foreach (var value in values)
        {
            if (value < low) low = value;
            if (value > high) high = value;
        }

        if (high - low < 0.0001)
        {
            // A perfectly flat series still has to be drawn somewhere in the middle rather than on the floor.
            double centre = low;
            return (centre - 1, centre + 1);
        }

        double padding = (high - low) * 0.1;
        return (low - padding, high + padding);
    }
}

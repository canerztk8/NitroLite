using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// The monitoring page's history chart: a line, a wash under it, a dot on the newest sample, and three grid rules.
/// </summary>
/// <remarks>
/// <para>
/// A separate control from <see cref="SparklineChart"/> because the two answer different questions. The sparkline is a
/// glance — no axes, no labels, the data's own range. This one is a reading: the range is fixed by the caller so two
/// minutes of the same series are comparable, the grid rules are drawn where the gutter's labels sit, and the newest
/// sample is marked because that is the number the strip below repeats.
/// </para>
/// <para>
/// The colours are styled properties, so the view binds them to the palette and a theme change repaints the chart with
/// everything else. The control knows nothing about themes.
/// </para>
/// </remarks>
public sealed class TelemetryChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<TelemetryChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<TelemetryChart, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TelemetryChart, double>(nameof(Maximum));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TelemetryChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TelemetryChart, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> GridProperty =
        AvaloniaProperty.Register<TelemetryChart, IBrush?>(nameof(Grid));

    public static readonly StyledProperty<IBrush?> DotProperty =
        AvaloniaProperty.Register<TelemetryChart, IBrush?>(nameof(Dot));

    public static readonly StyledProperty<IBrush?> DotRingProperty =
        AvaloniaProperty.Register<TelemetryChart, IBrush?>(nameof(DotRing));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<TelemetryChart, double>(nameof(StrokeThickness), 1.5);

    static TelemetryChart()
    {
        AffectsRender<TelemetryChart>(
            ValuesProperty, MinimumProperty, MaximumProperty, StrokeProperty, FillProperty, GridProperty,
            DotProperty, DotRingProperty, StrokeThicknessProperty);
    }

    /// <summary>The samples to draw, oldest first. Fewer than two draws the grid only.</summary>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>The bottom of the fixed range. The caller owns it, because it comes from the selected series.</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The three horizontal rules, drawn at a third, a half and two thirds of the height.</summary>
    public IBrush? Grid
    {
        get => GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    /// <summary>The newest sample's dot.</summary>
    public IBrush? Dot
    {
        get => GetValue(DotProperty);
        set => SetValue(DotProperty, value);
    }

    /// <summary>The ring around the dot, so it reads as a marker rather than a blob on the line.</summary>
    public IBrush? DotRing
    {
        get => GetValue(DotRingProperty);
        set => SetValue(DotRingProperty, value);
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

        DrawGrid(context, width, height);

        var values = Values;
        if (values == null || values.Count < 2) return;

        double low = Minimum;
        double high = Maximum;
        if (high - low < 0.0001) return;

        double inset = Math.Max(1, StrokeThickness / 2);
        double plotHeight = Math.Max(1, height - inset * 2);

        var points = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            double x = i / (double)(values.Count - 1) * width;
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

        // The newest sample is the number the strip below repeats, so it gets a marker. The ring is drawn first and
        // slightly larger, which is what keeps the dot legible where the line runs through it.
        var last = points[^1];
        var ring = DotRing;
        if (ring != null) context.DrawEllipse(ring, null, last, 4.5, 4.5);
        var dot = Dot;
        if (dot != null) context.DrawEllipse(dot, null, last, 3, 3);
    }

    /// <summary>
    /// The three rules. They sit at a third, a half and two thirds rather than at even quarters because the gutter's
    /// labels are placed against them, and three labels over a 200px box is as many as stays readable.
    /// </summary>
    private void DrawGrid(DrawingContext context, double width, double height)
    {
        var brush = Grid;
        if (brush == null) return;

        var pen = new Pen(brush, 1);
        foreach (double fraction in new[] { 1.0 / 3.0, 0.5, 2.0 / 3.0 })
        {
            double y = Math.Round(height * fraction) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }
}

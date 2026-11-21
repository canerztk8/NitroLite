using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// The monitoring page's history chart: a line, a wash under it, a dot on the newest sample, three grid rules, and a
/// vertical rule wherever the operating mode changed.
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

    /// <summary>Where the operating mode changed, as fractions of the width from 0 to 1.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> MarksProperty =
        AvaloniaProperty.Register<TelemetryChart, IReadOnlyList<double>?>(nameof(Marks));

    public static readonly StyledProperty<IBrush?> MarkProperty =
        AvaloniaProperty.Register<TelemetryChart, IBrush?>(nameof(Mark));

    static TelemetryChart()
    {
        AffectsRender<TelemetryChart>(
            ValuesProperty, MinimumProperty, MaximumProperty, StrokeProperty, FillProperty, GridProperty,
            DotProperty, DotRingProperty, StrokeThicknessProperty, MarksProperty, MarkProperty);
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

    /// <summary>
    /// The operating-mode changes, as fractions of the width. Each one is drawn as a full-height vertical rule, so the
    /// series can be read either side of it: the question a mode change raises is whether the line has been falling
    /// since, and a rule is what makes "since" visible.
    /// </summary>
    public IReadOnlyList<double>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>The rule colour. The view binds it to the accent, so a mode change is the loudest thing on the chart.</summary>
    public IBrush? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>The point buffer, reused between paints. See the comment where it is grown.</summary>
    private Point[] _points = [];

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        DrawGrid(context, width, height);

        // Under the series rather than over it: the rule is a divider, and a divider that hides the reading it divides
        // is worse than no divider.
        DrawMarks(context, width, height);

        var values = Values;
        if (values == null || values.Count < 2) return;

        double low = Minimum;
        double high = Maximum;
        if (high - low < 0.0001) return;

        double inset = Math.Max(1, StrokeThickness / 2);
        double plotHeight = Math.Max(1, height - inset * 2);

        // One buffer for the life of the control, grown to the widest series it has been handed: the chart is painted
        // once per telemetry tick for as long as the page is open, and a fresh array per paint was an allocation the
        // user never sees the result of.
        int count = values.Count;
        if (_points.Length < count) _points = new Point[count];
        var points = _points;

        for (int i = 0; i < count; i++)
        {
            double x = i / (double)(count - 1) * width;
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
                for (int i = 0; i < count; i++) sink.LineTo(points[i]);
                sink.LineTo(new Point(points[count - 1].X, height));
                sink.EndFigure(true);
            }

            context.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var sink = line.Open())
        {
            sink.BeginFigure(points[0], false);
            for (int i = 1; i < count; i++) sink.LineTo(points[i]);
            sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Stroke ?? Brushes.White, StrokeThickness), line);

        // The newest sample is the number the strip below repeats, so it gets a marker. The ring is drawn first and
        // slightly larger, which is what keeps the dot legible where the line runs through it.
        var last = points[count - 1];
        var ring = DotRing;
        if (ring != null) context.DrawEllipse(ring, null, last, 4.5, 4.5);
        var dot = Dot;
        if (dot != null) context.DrawEllipse(dot, null, last, 3, 3);
    }

    /// <summary>
    /// One vertical rule per mode change, full height. A short tick at the top would be lost against the line; a full
    /// one reads as a divider, which is what a mode change is.
    /// </summary>
    private void DrawMarks(DrawingContext context, double width, double height)
    {
        var brush = Mark;
        var marks = Marks;
        if (brush == null || marks == null || marks.Count == 0) return;

        var pen = new Pen(brush, 1);
        foreach (var fraction in marks)
        {
            double x = Math.Round(Math.Clamp(fraction, 0, 1) * width) + 0.5;
            context.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
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

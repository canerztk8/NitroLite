using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// Draws the recorded battery wear readings as a line, with the newest reading marked.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="SparklineChart"/> for two reasons. The wear curve is a history, not a live trace: its x
/// axis is a date range that can span months, and its y axis is a percentage that must not be rescaled on every
/// reading or the same curve would look steeper as the pack aged. And the newest point carries a dot, because the
/// question the card answers is "where is it now", not "what shape is this".
/// </para>
/// <para>
/// The colours are styled properties so the view binds them to the palette with <c>DynamicResource</c>; the control
/// itself knows nothing about themes.
/// </para>
/// </remarks>
public sealed class WearTrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<WearTrendChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<WearTrendChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<WearTrendChart, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> MarkerProperty =
        AvaloniaProperty.Register<WearTrendChart, IBrush?>(nameof(Marker));

    public static readonly StyledProperty<IBrush?> GridProperty =
        AvaloniaProperty.Register<WearTrendChart, IBrush?>(nameof(Grid));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<WearTrendChart, double>(nameof(StrokeThickness), 1.6);

    static WearTrendChart()
    {
        AffectsRender<WearTrendChart>(
            ValuesProperty, StrokeProperty, FillProperty, MarkerProperty, GridProperty, StrokeThicknessProperty);
    }

    /// <summary>The wear percentages, oldest first. Fewer than two draws the placeholder rule.</summary>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

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

    /// <summary>The dot on the newest reading.</summary>
    public IBrush? Marker
    {
        get => GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    /// <summary>The faint horizontal rules behind the curve.</summary>
    public IBrush? Grid
    {
        get => GetValue(GridProperty);
        set => SetValue(GridProperty, value);
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
        if (values == null || values.Count < 2)
        {
            DrawPlaceholder(context, width, height);
            return;
        }

        var (low, high) = Range(values);

        double inset = Math.Max(2, StrokeThickness);
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

        // The newest reading is the live one, so it gets the dot the other traces use.
        if (Marker is { } marker)
        {
            context.DrawEllipse(marker, null, points[^1], 2.5, 2.5);
        }
    }

    /// <summary>Three faint rules, so the curve has a scale to be read against.</summary>
    private void DrawGrid(DrawingContext context, double width, double height)
    {
        if (Grid is not { } grid) return;

        var pen = new Pen(grid, 1);
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(height * i / 4.0) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }

    /// <summary>
    /// The rule drawn while there is not yet a curve. A single reading is a number, not a trend, and an empty rectangle
    /// would read as a broken chart rather than as a history waiting for its second sample.
    /// </summary>
    private void DrawPlaceholder(DrawingContext context, double width, double height)
    {
        if (Grid is not { } grid) return;

        double y = Math.Round(height / 2) + 0.5;
        var pen = new Pen(grid, 1, new DashStyle(new double[] { 3, 3 }, 0));
        context.DrawLine(pen, new Point(0, y), new Point(width, y));
    }

    /// <summary>
    /// The vertical range. Padded by a tenth of the spread, with a floor of half a point so a flat history is drawn as
    /// a flat line in the middle rather than dividing by zero.
    /// </summary>
    private static (double Low, double High) Range(IReadOnlyList<double> values)
    {
        double low = double.MaxValue;
        double high = double.MinValue;
        foreach (var value in values)
        {
            if (value < low) low = value;
            if (value > high) high = value;
        }

        double padding = Math.Max(0.5, (high - low) * 0.15);
        double bottom = Math.Max(0, low - padding);
        double top = high + padding;
        if (top - bottom < 1.0) top = bottom + 1.0;
        return (bottom, top);
    }
}

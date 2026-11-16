using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// The mode trace: the last minute of CPU temperature as a thin strip, with a tick wherever the operating mode changed.
/// </summary>
/// <remarks>
/// <para>
/// The point of the strip is the relationship, not either series. A temperature line on its own says the machine got
/// hot; a tick on the same axis says the machine got hot <em>after</em> Turbo was selected, which is the thing a user
/// can act on. That is why the two are drawn in one control rather than as two charts stacked.
/// </para>
/// <para>
/// The marks arrive as fractions of the width rather than as timestamps: the view model owns the window and the clock,
/// and a control that re-derived them from <see cref="DateTime.Now"/> would draw a different strip on every repaint.
/// </para>
/// </remarks>
public sealed class ModeTraceChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<ModeTraceChart, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Where the mode changed, as fractions of the width from 0 to 1.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> MarksProperty =
        AvaloniaProperty.Register<ModeTraceChart, IReadOnlyList<double>?>(nameof(Marks));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ModeTraceChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ModeTraceChart, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> MarkProperty =
        AvaloniaProperty.Register<ModeTraceChart, IBrush?>(nameof(Mark));

    public static readonly StyledProperty<IBrush?> BaselineProperty =
        AvaloniaProperty.Register<ModeTraceChart, IBrush?>(nameof(Baseline));

    static ModeTraceChart()
    {
        AffectsRender<ModeTraceChart>(
            ValuesProperty, MarksProperty, StrokeProperty, FillProperty, MarkProperty, BaselineProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
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

    /// <summary>The tick colour. The view binds it to the accent, so a mode change is the loudest thing on the strip.</summary>
    public IBrush? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>The faint rule drawn while there is not yet a series, so the card is never an empty rectangle.</summary>
    public IBrush? Baseline
    {
        get => GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        var values = Values;
        if (values == null || values.Count < 2)
        {
            DrawBaseline(context, width, height);
            DrawMarks(context, width, height);
            return;
        }

        var (low, high) = Range(values);
        double inset = 1;
        double plotHeight = Math.Max(1, height - inset * 2);

        var points = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            double x = i / (double)(values.Count - 1) * width;
            double ratio = high - low < 0.0001 ? 0.5 : Math.Clamp((values[i] - low) / (high - low), 0, 1);
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

        context.DrawGeometry(null, new Pen(Stroke ?? Brushes.White, 1.2), line);

        DrawMarks(context, width, height);
    }

    /// <summary>
    /// One vertical tick per mode change, full height. A short tick at the top would be lost against the line; a full
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

    private void DrawBaseline(DrawingContext context, double width, double height)
    {
        var brush = Baseline;
        if (brush == null) return;

        double y = Math.Round(height / 2) + 0.5;
        var pen = new Pen(brush, 1, new DashStyle(new double[] { 3, 3 }, 0));
        context.DrawLine(pen, new Point(0, y), new Point(width, y));
    }

    /// <summary>
    /// The strip's own range, padded by a tenth. Unlike the history chart this one is not fixed: the strip is a shape,
    /// and a 3 °C drift has to be visible or the ticks are drawn against a flat line.
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

        if (high - low < 0.0001) return (low - 1, low + 1);

        double padding = (high - low) * 0.1;
        return (low - padding, high + padding);
    }
}

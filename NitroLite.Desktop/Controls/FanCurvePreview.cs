using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NitroLite.Core;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// Draws the fan curve, the thermal safety bands and the current CPU temperature on one canvas.
/// </summary>
/// <remarks>
/// <para>
/// Read-only on purpose. The curve is edited with the six sliders next to it, which means the drawing cannot disagree
/// with the values that will be written: it renders exactly the points the sliders hold, and the safety bands are
/// drawn from <see cref="FanCurve.SafetyFloor"/> rather than from a copy of the numbers.
/// </para>
/// <para>
/// The shaded bands are the point of the control. A user who drags the 90 °C anchor down to 40 % sees the line sit
/// below the band and understands why the fans will not go that low, without having to read a paragraph about it.
/// </para>
/// <para>
/// Every colour is a styled property rather than a constant, so the view binds them to the active palette. The
/// defaults are the dark palette's own values, which keeps the control usable on its own in a designer or a test.
/// </para>
/// </remarks>
public sealed class FanCurvePreview : Control
{
    public static readonly StyledProperty<IReadOnlyList<FanCurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurvePreview, IReadOnlyList<FanCurvePoint>?>(nameof(Points));

    public static readonly StyledProperty<double> CurrentTemperatureProperty =
        AvaloniaProperty.Register<FanCurvePreview, double>(nameof(CurrentTemperature));

    /// <summary>The plate the plot is drawn on.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(SurfaceBrush), new SolidColorBrush(Color.Parse("#11161D")));

    /// <summary>The tint behind a thermal safety band.</summary>
    public static readonly StyledProperty<IBrush?> BandBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(BandBrush), new SolidColorBrush(Color.Parse("#3A1F24")));

    /// <summary>The curve and its anchors.</summary>
    public static readonly StyledProperty<IBrush?> CurveBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(CurveBrush), new SolidColorBrush(Color.Parse("#2F81F7")));

    /// <summary>The vertical line at the current temperature.</summary>
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(MarkerBrush), new SolidColorBrush(Color.Parse("#F2A33A")));

    /// <summary>The grid lines.</summary>
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(GridBrush), new SolidColorBrush(Color.Parse("#1E2530")));

    /// <summary>The axis labels.</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(TextBrush), new SolidColorBrush(Color.Parse("#8A94A6")));

    /// <summary>The dashed line at the minimum the fans will accept for a band.</summary>
    public static readonly StyledProperty<IBrush?> FloorBrushProperty =
        AvaloniaProperty.Register<FanCurvePreview, IBrush?>(
            nameof(FloorBrush), new SolidColorBrush(Color.Parse("#E5484D")));

    static FanCurvePreview()
    {
        AffectsRender<FanCurvePreview>(
            PointsProperty,
            CurrentTemperatureProperty,
            SurfaceBrushProperty,
            BandBrushProperty,
            CurveBrushProperty,
            MarkerBrushProperty,
            GridBrushProperty,
            TextBrushProperty,
            FloorBrushProperty);
    }

    /// <summary>The anchors to draw. Null or empty draws the grid only.</summary>
    public IReadOnlyList<FanCurvePoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>The CPU temperature to mark, or 0 for no marker.</summary>
    public double CurrentTemperature
    {
        get => GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    public IBrush? SurfaceBrush
    {
        get => GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public IBrush? BandBrush
    {
        get => GetValue(BandBrushProperty);
        set => SetValue(BandBrushProperty, value);
    }

    public IBrush? CurveBrush
    {
        get => GetValue(CurveBrushProperty);
        set => SetValue(CurveBrushProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? FloorBrush
    {
        get => GetValue(FloorBrushProperty);
        set => SetValue(FloorBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        // The bottom strip belongs to the temperature axis, so the plot stops above it and every Y is measured
        // against the plot height rather than the control's.
        double plotHeight = Math.Max(1, height - AxisHeight);
        var plot = new Rect(0, 0, width, plotHeight);
        if (SurfaceBrush is IBrush surface) context.FillRectangle(surface, plot, 8);

        // The safety bands, drawn first so the curve sits on top of them.
        foreach (var (bandTemperature, bandPercent) in FanCurve.SafetyFloor)
        {
            double x = XFor(bandTemperature, width);
            double right = bandTemperature >= FanCurve.SafetyFloor[^1].TemperatureC ? width : XFor(bandTemperature + 5, width);
            if (BandBrush is IBrush band) context.FillRectangle(band, new Rect(x, 0, Math.Max(1, right - x), plotHeight));

            double y = YFor(bandPercent, plotHeight);
            if (FloorBrush is IBrush floor)
            {
                context.DrawLine(
                    new Pen(floor, 1, new DashStyle(new double[] { 3, 3 }, 0)),
                    new Point(x, y),
                    new Point(right, y));
            }
        }

        // Grid: one line per 20 % and per 20 °C, so a value can be read off without a tooltip.
        var gridPen = new Pen(GridBrush, 1);
        for (int percent = 20; percent < 100; percent += 20)
        {
            double y = YFor(percent, plotHeight);
            context.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        for (int temperature = 40; temperature <= 90; temperature += 10)
        {
            double x = XFor(temperature, width);
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, plotHeight));
        }

        var points = Points;
        if (points == null || points.Count < 2) return;

        // The curve itself, with the safety floor applied exactly as the write path applies it.
        var curve = new FanCurve(points);
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            bool started = false;
            for (double temperature = FanCurve.MinTemperatureC; temperature <= FanCurve.MaxTemperatureC; temperature += 1)
            {
                var point = new Point(XFor(temperature, width), YFor(curve.PercentAt(temperature), plotHeight));
                if (!started)
                {
                    sink.BeginFigure(point, false);
                    started = true;
                }
                else
                {
                    sink.LineTo(point);
                }
            }

            if (started) sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(CurveBrush, 2), geometry);

        // The anchors, so the sliders and the drawing are visibly the same six numbers.
        foreach (var anchor in points)
        {
            var centre = new Point(XFor(anchor.TemperatureC, width), YFor(anchor.Percent, plotHeight));
            context.DrawEllipse(CurveBrush, null, centre, 3.5, 3.5);
        }

        if (CurrentTemperature > 0)
        {
            double x = XFor(CurrentTemperature, width);
            context.DrawLine(new Pen(MarkerBrush, 1.5), new Point(x, 0), new Point(x, plotHeight));
            DrawLabel(context, $"{CurrentTemperature:0} °C", new Point(Math.Min(x + 4, width - 44), 4));
        }

        DrawLabel(context, "100 %", new Point(4, 2));
        DrawLabel(context, "30 %", new Point(4, plotHeight - 16));

        // The temperature axis. Without it the vertical grid lines are decoration; with it a user can read which
        // anchor the marker is near. The first and last labels are nudged inwards so they are not clipped.
        for (int temperature = 40; temperature <= 90; temperature += 10)
        {
            string text = $"{temperature} °C";
            double textWidth = Measure(text);
            double x = Math.Clamp(XFor(temperature, width) - textWidth / 2, 0, Math.Max(0, width - textWidth));
            DrawLabel(context, text, new Point(x, plotHeight + 3));
        }
    }

    /// <summary>The strip at the bottom reserved for the temperature labels.</summary>
    private const double AxisHeight = 16;

    private double Measure(string text) => Format(text).Width;

    private FormattedText Format(string text) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        Typeface.Default,
        10,
        TextBrush);

    private static double XFor(double temperatureC, double width)
    {
        double span = FanCurve.MaxTemperatureC - FanCurve.MinTemperatureC;
        double ratio = (temperatureC - FanCurve.MinTemperatureC) / span;
        return Math.Clamp(ratio, 0, 1) * width;
    }

    private static double YFor(double percent, double height)
    {
        double span = FanCurve.MaxPercent - FanCurve.MinPercent;
        double ratio = (percent - FanCurve.MinPercent) / span;
        return height - Math.Clamp(ratio, 0, 1) * height;
    }

    private void DrawLabel(DrawingContext context, string text, Point origin) =>
        context.DrawText(Format(text), origin);
}

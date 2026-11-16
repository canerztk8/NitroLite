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
/// </remarks>
public sealed class FanCurvePreview : Control
{
    public static readonly StyledProperty<IReadOnlyList<FanCurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurvePreview, IReadOnlyList<FanCurvePoint>?>(nameof(Points));

    public static readonly StyledProperty<double> CurrentTemperatureProperty =
        AvaloniaProperty.Register<FanCurvePreview, double>(nameof(CurrentTemperature));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#11161D"));
    private static readonly IBrush BandBrush = new SolidColorBrush(Color.Parse("#3A1F24"));
    private static readonly IBrush CurveBrush = new SolidColorBrush(Color.Parse("#2F81F7"));
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.Parse("#F2A33A"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#1E2530"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#8A94A6"));
    private static readonly IBrush FloorBrush = new SolidColorBrush(Color.Parse("#E5484D"));

    static FanCurvePreview()
    {
        AffectsRender<FanCurvePreview>(PointsProperty, CurrentTemperatureProperty);
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

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        // The bottom strip belongs to the temperature axis, so the plot stops above it and every Y is measured
        // against the plot height rather than the control's.
        double plotHeight = Math.Max(1, height - AxisHeight);
        var plot = new Rect(0, 0, width, plotHeight);
        context.FillRectangle(Background, plot, 8);

        // The safety bands, drawn first so the curve sits on top of them.
        foreach (var (bandTemperature, bandPercent) in FanCurve.SafetyFloor)
        {
            double x = XFor(bandTemperature, width);
            double right = bandTemperature >= FanCurve.SafetyFloor[^1].TemperatureC ? width : XFor(bandTemperature + 5, width);
            context.FillRectangle(BandBrush, new Rect(x, 0, Math.Max(1, right - x), plotHeight));

            double y = YFor(bandPercent, plotHeight);
            context.DrawLine(new Pen(FloorBrush, 1, new DashStyle(new double[] { 3, 3 }, 0)), new Point(x, y), new Point(right, y));
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

    private static double Measure(string text) => Format(text).Width;

    private static FormattedText Format(string text) => new(
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

    private static void DrawLabel(DrawingContext context, string text, Point origin) =>
        context.DrawText(Format(text), origin);
}

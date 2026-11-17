
namespace NitroLite.Core;

/// <summary>
/// A named shape for the fan curve: the six anchors the editor offers, filled in with one click.
/// <para>
/// A preset is a shortcut, not a mode. Applying one writes the same six values the sliders hold, so nothing
/// downstream has to know a preset was used, and moving a slider afterwards simply leaves the preset behind.
/// That is also why no preset id is stored in the settings file: the shape is the state.
/// </para>
/// </summary>
public sealed record FanCurvePreset(string Id, string NameKey, string DescriptionKey, IReadOnlyList<int> Percents)
{
    /// <summary>The preset as curve anchors, one per temperature the editor offers.</summary>
    public List<FanCurvePoint> ToPoints()
    {
        var points = new List<FanCurvePoint>(Percents.Count);
        for (int i = 0; i < Percents.Count && i < FanCurve.AnchorTemperatures.Count; i++)
        {
            points.Add(new FanCurvePoint(FanCurve.AnchorTemperatures[i], Percents[i]));
        }
        return points;
    }
}

/// <summary>
/// The curve shapes offered above the editor. Three, because they answer three different questions: "leave it
/// roughly as it was", "make it quieter", "keep it cooler". A fourth shape that is loud from idle would only
/// duplicate the fan card's own Maximum mode, which is simpler and does the same thing.
/// </summary>
public static class FanCurvePresets
{
    public static IReadOnlyList<FanCurvePreset> All { get; } = new[]
    {
        new FanCurvePreset(
            "balanced",
            Loc.T("Dengeli"),
            Loc.T("Fabrika eğrisine en yakın şekil: sessiz başlar, 80 °C'den sonra hızlanır."),
            new[] { 30, 32, 36, 42, 62, 100 }),

        new FanCurvePreset(
            "quiet",
            Loc.T("Sessiz"),
            Loc.T("Orta sıcaklıklarda daha yavaş döner. 85 °C ve üstünde güvenlik sınırı yine devreye girer."),
            new[] { 30, 30, 30, 34, 50, 100 }),

        new FanCurvePreset(
            "gaming",
            Loc.T("Oyun"),
            Loc.T("Daha erken hızlanır ve sıcaklığı düşük tutar; Dengeli'den belirgin şekilde daha gürültülü."),
            new[] { 30, 38, 50, 65, 85, 100 })
    };

    /// <summary>The preset with this id, or null when the id is unknown or missing.</summary>
    public static FanCurvePreset? ById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The preset whose shape these anchors are, or null when the curve is the user's own. Compared on the
    /// editor's own temperatures, so a curve edited by a single point stops claiming a preset.
    /// </summary>
    public static FanCurvePreset? Match(IEnumerable<FanCurvePoint>? points)
    {
        if (points == null) return null;

        var byTemperature = new Dictionary<int, int>();
        foreach (var point in points)
        {
            if (point == null) continue;
            byTemperature[point.TemperatureC] = point.Percent;
        }

        // A curve with an anchor somewhere else is not any of these shapes, whatever its values are.
        if (byTemperature.Count != FanCurve.AnchorTemperatures.Count) return null;

        foreach (var preset in All)
        {
            bool same = true;
            for (int i = 0; i < FanCurve.AnchorTemperatures.Count; i++)
            {
                if (!byTemperature.TryGetValue(FanCurve.AnchorTemperatures[i], out int percent) ||
                    percent != preset.Percents[i])
                {
                    same = false;
                    break;
                }
            }
            if (same) return preset;
        }

        return null;
    }
}

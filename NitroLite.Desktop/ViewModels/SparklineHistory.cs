namespace NitroLite.Desktop.ViewModels;

/// <summary>
/// A fixed-length window over the most recent telemetry samples, oldest first.
/// </summary>
/// <remarks>
/// <para>
/// A ring buffer rather than a list with a <c>RemoveAt(0)</c>: the telemetry loop runs once a second for as long as the
/// window is open, and shifting sixty doubles every second is work that buys nothing. The buffer is allocated once and
/// never grows.
/// </para>
/// <para>
/// <see cref="Snapshot"/> returns a fresh array every call. That is deliberate: the chart is a styled property, and
/// Avalonia only re-renders when the property value changes. Handing back the same list instance after appending to it
/// would leave the chart drawing the first sixty samples forever.
/// </para>
/// </remarks>
public sealed class SparklineHistory
{
    /// <summary>How many samples the dashboard charts keep: one minute at the telemetry loop's one-second tick.</summary>
    public const int DefaultCapacity = 60;

    private readonly double[] _values;
    private int _next;
    private int _count;

    public SparklineHistory(int capacity = DefaultCapacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "a chart needs two points");

        _values = new double[capacity];
    }

    /// <summary>How many samples fit before the oldest is dropped.</summary>
    public int Capacity => _values.Length;

    /// <summary>How many samples are held right now. Never more than <see cref="Capacity"/>.</summary>
    public int Count => _count;

    /// <summary>
    /// Appends a sample, dropping the oldest once the window is full. A NaN or an infinity is ignored rather than
    /// stored: it would poison the chart's own minimum and maximum and flatten every real sample against it.
    /// </summary>
    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        _values[_next] = value;
        _next = (_next + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    /// <summary>Forgets every sample. Used when the backend changes under the window.</summary>
    public void Clear()
    {
        _next = 0;
        _count = 0;
    }

    /// <summary>The samples, oldest first. A new array each call, so a binding sees a changed value.</summary>
    public IReadOnlyList<double> Snapshot()
    {
        if (_count == 0) return Array.Empty<double>();

        var result = new double[_count];
        int start = (_next - _count + _values.Length) % _values.Length;
        for (int i = 0; i < _count; i++) result[i] = _values[(start + i) % _values.Length];
        return result;
    }
}

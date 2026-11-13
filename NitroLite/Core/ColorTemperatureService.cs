using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NitroLite.Core;

/// <summary>
/// Shifts the display white point towards warm so a screen is easier to read at night. Works through the display
/// gamma ramp, which is a GDI call every Windows machine has: no driver, no overlay, nothing for an anti-cheat to
/// look at. The ramp is per monitor, so a laptop panel and an external screen are both covered.
/// <para>
/// 6500 K is neutral and writes the identity ramp, which is also what turning the feature off does. The ramp that
/// was there before the first write is captured and put back on exit, so NitroLite does not leave a screen tinted.
/// A crash between the two leaves the tint in place until the next launch or a display mode change; the reset
/// button exists for exactly that case.
/// </para>
/// </summary>
public class ColorTemperatureService : IDisposable
{
    /// <summary>No tint. Above this the curve has nothing to say, so the identity ramp is written.</summary>
    public const int NeutralKelvin = 6500;

    /// <summary>As warm as the slider goes: candlelight, still readable.</summary>
    public const int MinKelvin = 1900;

    public const int MaxKelvin = NeutralKelvin;

    private const int DisplayDeviceAttachedToDesktop = 0x00000001;

    private readonly Dictionary<string, ushort[]> _savedRamps = new();

    /// <summary>The temperature last written, or <see cref="NeutralKelvin"/> when nothing is applied.</summary>
    public int CurrentKelvin { get; protected set; } = NeutralKelvin;

    /// <summary>True once a ramp has been written and not yet restored.</summary>
    public bool IsApplied { get; protected set; }

    /// <summary>
    /// The per-channel multipliers for a temperature, normalised so the brightest channel is 1 and 6500 K is
    /// exactly (1, 1, 1). Pure: no display, no state, so the colour maths can be tested on its own.
    /// </summary>
    public static (double R, double G, double B) MultipliersFor(int kelvin)
    {
        if (kelvin >= NeutralKelvin) return (1, 1, 1);

        int clamped = Math.Clamp(kelvin, MinKelvin, NeutralKelvin);
        var target = BlackbodyRgb(clamped);
        var neutral = BlackbodyRgb(NeutralKelvin);

        double r = target.R / neutral.R;
        double g = target.G / neutral.G;
        double b = target.B / neutral.B;

        double max = Math.Max(r, Math.Max(g, b));
        if (max <= 0 || double.IsNaN(max) || double.IsInfinity(max)) return (1, 1, 1);

        return (Math.Clamp(r / max, 0, 1), Math.Clamp(g / max, 0, 1), Math.Clamp(b / max, 0, 1));
    }

    /// <summary>
    /// The 3 × 256 ramp <c>SetDeviceGammaRamp</c> expects: red, then green, then blue, each mapping an input level
    /// to an output level. A multiplier of 1 produces the identity ramp, which is what Windows writes itself.
    /// </summary>
    public static ushort[] BuildGammaRamp(int kelvin)
    {
        var (r, g, b) = MultipliersFor(kelvin);
        var ramp = new ushort[768];
        WriteChannel(ramp, 0, r);
        WriteChannel(ramp, 256, g);
        WriteChannel(ramp, 512, b);
        return ramp;
    }

    private static void WriteChannel(ushort[] ramp, int offset, double multiplier)
    {
        double m = Math.Clamp(multiplier, 0, 1);
        for (int i = 0; i < 256; i++)
        {
            // 65535 / 255 = 257, so the identity ramp is exactly i * 257.
            ramp[offset + i] = (ushort)Math.Clamp(Math.Round(i * 257.0 * m), 0, 65535);
        }
    }

    /// <summary>
    /// Writes the ramp for <paramref name="kelvin"/> to every attached display. Returns false when no display
    /// accepted it, which is what a remote session or a driver that refuses gamma changes looks like.
    /// </summary>
    public virtual bool Apply(int kelvin)
    {
        int clamped = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
        var ramp = BuildGammaRamp(clamped);

        bool any = ForEachDisplay((name, hdc) =>
        {
            if (!_savedRamps.ContainsKey(name))
            {
                var original = new ushort[768];
                if (GetDeviceGammaRamp(hdc, original)) _savedRamps[name] = original;
            }
            return SetDeviceGammaRamp(hdc, ramp);
        });

        if (any)
        {
            IsApplied = true;
            CurrentKelvin = clamped;
        }
        return any;
    }

    /// <summary>
    /// Writes the neutral ramp without recording it as the ramp to restore. Called at startup so a tint left
    /// behind by a crash or a force-kill is not mistaken for the screen's own state: without this, the ramp
    /// captured by the next <see cref="Apply"/> would be the stale tint and turning the feature off would keep it.
    /// </summary>
    public virtual void ResetToNeutral()
    {
        var ramp = BuildGammaRamp(NeutralKelvin);
        ForEachDisplay((_, hdc) => SetDeviceGammaRamp(hdc, ramp));

        _savedRamps.Clear();
        IsApplied = false;
        CurrentKelvin = NeutralKelvin;
    }

    /// <summary>
    /// Puts back the ramp that was there before the first <see cref="Apply"/>. Safe to call when nothing was
    /// applied, and safe to call twice.
    /// </summary>
    public virtual void Restore()
    {
        if (_savedRamps.Count > 0)
        {
            ForEachDisplay((name, hdc) =>
                _savedRamps.TryGetValue(name, out var original) && SetDeviceGammaRamp(hdc, original));
        }

        _savedRamps.Clear();
        IsApplied = false;
        CurrentKelvin = NeutralKelvin;
    }

    public void Dispose() => Restore();

    /// <summary>
    /// Runs <paramref name="action"/> once per attached display with a device context for it. Falls back to the
    /// screen DC when enumeration finds nothing, so a machine with an unusual driver still gets one attempt.
    /// </summary>
    private static bool ForEachDisplay(Func<string, IntPtr, bool> action)
    {
        bool any = false;
        var names = AttachedDisplayNames().ToList();

        if (names.Count == 0)
        {
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                return action("primary", screen);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        foreach (var name in names)
        {
            var hdc = CreateDC(name, null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) continue;
            try
            {
                if (action(name, hdc)) any = true;
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        return any;
    }

    private static IEnumerable<string> AttachedDisplayNames()
    {
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref device, 0)) yield break;
            if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
            if (string.IsNullOrEmpty(device.DeviceName)) continue;
            yield return device.DeviceName;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>
    /// Tanner Helland's approximation of a blackbody's sRGB colour, clamped to the range it is valid over. Only
    /// the ratio between two temperatures matters here, so the absolute scale cancels out.
    /// </summary>
    private static (double R, double G, double B) BlackbodyRgb(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r, g, b;

        if (t <= 66) r = 255;
        else r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);

        if (t <= 66) g = 99.4708025861 * Math.Log(t) - 161.1195681661;
        else g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);

        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;

        return (Math.Clamp(r / 255, 0, 1), Math.Clamp(g / 255, 0, 1), Math.Clamp(b / 255, 0, 1));
    }

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? driver, string? device, string? port, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DisplayDevice displayDevice, uint flags);
}

namespace NitroLite.Core;

/// <summary>
/// The keyboard lighting configuration as the Acer service reports and accepts it: one effect, a brightness, a
/// speed, a direction, a primary colour and up to four zone colours.
/// </summary>
/// <remarks>
/// A plain data record with no behaviour, so it belongs with the hardware contract rather than with the socket
/// client that happens to fill it in. The direct-WMI and OpenRGB backends produce the same shape.
/// </remarks>
public class LightingConfig
{
    public string Effect { get; set; } = "WAVE";
    public int Brightness { get; set; } = 5;
    public int Speed { get; set; } = 3;
    public int Direction { get; set; } = 1;
    public string Color { get; set; } = "#00E5FF";
    public List<string> LEDs { get; set; } = new() { "#00E5FF", "#00E5FF", "#00E5FF", "#00E5FF" };
}

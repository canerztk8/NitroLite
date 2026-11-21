using System.Text;

namespace NitroLite.Core.Hardware;

/// <summary>A field worksheet included in the report that already identifies the machine, OS and BIOS.</summary>
public static class PilotReport
{
    public static void Append(StringBuilder report, string temperatures, string rpm, string fan,
        string brightness, string zones, string modes, string mux, string charge)
    {
        report.AppendLine();
        report.AppendLine("| Pilot feature | Detection evidence | Physical observation |");
        report.AppendLine("| --- | --- | --- |");
        Row("Temperature", temperatures);
        Row("Fan RPM", rpm);
        Row("Fan: automatic", fan);
        Row("Fan: maximum", fan);
        Row("Fan: manual", fan);
        Row("Keyboard brightness", brightness);
        Row("Keyboard zones", zones);
        Row("Performance profile", modes);
        Row("GPU MUX", mux);
        Row("Battery charge limit", charge);
        report.AppendLine();

        void Row(string feature, string evidence) =>
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"| {feature} | {evidence} | Not recorded |");
    }
}

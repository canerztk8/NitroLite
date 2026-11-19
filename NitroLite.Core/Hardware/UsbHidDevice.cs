namespace NitroLite.Core.Hardware;

/// <summary>
/// One USB HID collection found on the machine: the vendor and product ids, the usage page and usage, and nothing
/// else. Built for the report, so it carries no serial number, no path and no user name.
/// </summary>
/// <remarks>
/// The keyboard on an Acer laptop is usually not the thing that lights up: the four-zone backlight is driven over
/// WMI, and the USB device that matters for compatibility is the one that does not answer WMI at all — an ITE
/// (<c>0x048D</c>) or Sonix (<c>0x0C45</c>) controller that a different tool would have to drive through HID reports.
/// This type is how a report says which one is plugged in, without asking the owner to run a USB viewer.
/// </remarks>
public sealed record UsbHidDevice
{
    /// <summary>USB vendor id, e.g. <c>0x048D</c> for ITE Tech.</summary>
    public int VendorId { get; init; }

    /// <summary>USB product id.</summary>
    public int ProductId { get; init; }

    /// <summary>The top-level collection's usage page, e.g. <c>0xFF00</c> for a vendor-defined one.</summary>
    public int UsagePage { get; init; }

    /// <summary>The top-level collection's usage inside that page.</summary>
    public int Usage { get; init; }

    /// <summary>A printable name the driver stack provided, trimmed of anything that could identify the machine.</summary>
    public string Name { get; init; } = "";

    /// <summary>The vendor behind <see cref="VendorId"/>, or a hex id when the table does not name it.</summary>
    public string Vendor => VendorName(VendorId);

    /// <summary>What the usage page and usage describe, in the words a triager needs.</summary>
    public string Category => CategoryName(UsagePage, Usage);

    /// <summary>True when this collection is a keyboard, a mouse or a consumer-control device.</summary>
    public bool IsInputDevice =>
        UsagePage == 0x01 && Usage is 0x02 or 0x06 or 0x07 or 0x0C;

    /// <summary>
    /// True for the vendor-defined collections the backlight controllers expose. These are the ones a future backend
    /// would have to talk to, so they are called out in the report rather than left in a list of every HID device.
    /// </summary>
    public bool IsVendorDefined => UsagePage is >= 0xFF00 and <= 0xFFFF;

    /// <summary>One report row: <c>ITE Tech · 0x048D:0xC985 · page 0xFF00 usage 0x0001</c>.</summary>
    public string Describe() =>
        $"{Vendor} · 0x{VendorId:X4}:0x{ProductId:X4} · page 0x{UsagePage:X4} usage 0x{Usage:X4} · {Category}";

    /// <summary>
    /// The vendor names worth recognising. Deliberately short: it exists so a report says "ITE Tech" instead of
    /// "0x048D", not to be a USB id database.
    /// </summary>
    public static string VendorName(int vendorId) => vendorId switch
    {
        0x048D => "ITE Tech",
        0x0C45 => "Sonix (SONiX)",
        0x04F2 => "Chicony",
        0x04F3 => "Elan",
        0x04D9 => "Holtek",
        0x0B05 => "ASUS",
        0x046D => "Logitech",
        0x045E => "Microsoft",
        0x1532 => "Razer",
        0x1038 => "SteelSeries",
        0x1A2C => "China Resource Semico",
        0x258A => "SINO WEALTH",
        0x05AC => "Apple",
        0x1B1C => "Corsair",
        0x0BDA => "Realtek",
        0x8087 => "Intel",
        0x0A12 => "Cambridge Silicon Radio",
        _ => "unnamed vendor"
    };

    /// <summary>The usage page and usage as a category, using the HID usage tables' own names where they exist.</summary>
    public static string CategoryName(int usagePage, int usage) => usagePage switch
    {
        0x01 => usage switch
        {
            0x02 => "mouse",
            0x06 => "keyboard",
            0x07 => "keypad",
            0x0C => "consumer control",
            0x80 => "system control",
            _ => "generic desktop"
        },
        0x0C => "consumer page",
        0x0B => "telephony",
        0x0D => "digitizer",
        0x07 => "LED page",
        0xFF00 => "vendor-defined",
        >= 0xFF00 and <= 0xFFFF => "vendor-defined (other page)",
        _ => "other"
    };

    /// <summary>
    /// A device name with anything that could carry a serial or a path removed: the driver stack appends
    /// <c>#4&amp;1a2b3c&amp;0&amp;0000</c> style instance strings to friendly names on some machines.
    /// </summary>
    public static string SanitiseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var text = name.Trim();
        int cut = text.IndexOf('#');
        if (cut > 0) text = text[..cut].Trim();
        return text.Length <= 60 ? text : text[..60].Trim();
    }
}

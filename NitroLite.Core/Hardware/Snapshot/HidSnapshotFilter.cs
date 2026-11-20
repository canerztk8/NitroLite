namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>
/// Which HID interfaces a capture is allowed to read a feature report from: the known Acer keyboard-lighting
/// controllers, on a vendor-defined lighting usage page, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The rule exists because a HID feature report is a read the firmware answers, and the interfaces on a laptop include
/// the one the keyboard types through. Reading a keyboard's own collection is pointless at best; the report the diff
/// wants belongs to the lighting controller beside it. So the standard input pages are refused by name — usage page
/// <c>0x01</c> is "Generic Desktop", which is where a keyboard (<c>0x06</c>) and a mouse live — rather than by hoping
/// the vendor filter is narrow enough.
/// </para>
/// <para>
/// The vendors are the three that ship in Nitro and Predator chassis: ITE Tech (<c>0x048D</c>), Sonix
/// (<c>0x0C45</c>) and Chicony (<c>0x04F2</c>). Their lighting lives on a vendor-defined page in the
/// <c>0xFF00</c>–<c>0xFF05</c> range, which is the range the RGB tools in the wild read.
/// </para>
/// </remarks>
public static class HidSnapshotFilter
{
    /// <summary>The controller vendors an Acer keyboard backlight is built from.</summary>
    public static IReadOnlyList<int> KnownVendors { get; } = new[] { 0x048D, 0x0C45, 0x04F2 };

    /// <summary>Usage page of a keyboard collection: <c>Generic Desktop</c>, usage <c>Keyboard</c>.</summary>
    public const int GenericDesktopPage = 0x01;

    /// <summary>The keyboard usage inside <see cref="GenericDesktopPage"/>.</summary>
    public const int KeyboardUsage = 0x06;

    /// <summary>Usage page of the LED collections, which belong to the keyboard that reports them.</summary>
    public const int LedPage = 0x07;

    /// <summary>
    /// True for the interface the keyboard itself types through. Never opened, never read, whatever vendor id the
    /// chassis happens to put on it.
    /// </summary>
    public static bool IsStandardKeyboardInterface(int usagePage, int usage) =>
        usagePage == GenericDesktopPage && usage == KeyboardUsage;

    /// <summary>
    /// True for the pages that describe an input device rather than a lighting controller: Generic Desktop (keyboard,
    /// mouse, consumer control) and the LED page.
    /// </summary>
    public static bool IsStandardInputPage(int usagePage) =>
        usagePage is GenericDesktopPage or LedPage;

    /// <summary>True for the vendor-defined page range a lighting feature report lives on.</summary>
    public static bool IsLightingUsagePage(int usagePage) => usagePage is >= 0xFF00 and <= 0xFF05;

    /// <summary>
    /// Whether this interface may be opened read-only for a feature-report capture: a known lighting vendor, on a
    /// vendor-defined lighting page, and never a standard input collection.
    /// </summary>
    public static bool IsEligible(int vendorId, int usagePage, int usage)
    {
        if (IsStandardInputPage(usagePage)) return false;
        if (IsStandardKeyboardInterface(usagePage, usage)) return false;
        if (!IsLightingUsagePage(usagePage)) return false;
        return vendorId is 0x048D or 0x0C45 or 0x04F2;
    }

    /// <summary>The entry key for one interface: <c>048D:C985/page0xFF00/usage0x0001</c>.</summary>
    public static string Key(int vendorId, int productId, int usagePage, int usage) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{vendorId:X4}:{productId:X4}/page0x{usagePage:X4}/usage0x{usage:X4}");
}

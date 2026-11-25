namespace NitroLite.Core.Hardware.Lighting;

/// <summary>
/// One vendor lighting controller, as documented by somebody who has one.
/// </summary>
/// <param name="VendorId">USB vendor id, e.g. <c>0x0CF2</c> for ENE.</param>
/// <param name="ProductId">USB product id, or 0 when the source only identifies the vendor.</param>
/// <param name="Name">The controller, e.g. <c>ENE KB5130</c>.</param>
/// <param name="Protocol">The documented frame family, e.g. <c>ene-kb5130</c>.</param>
/// <param name="Writable">True when a documented frame exists and may be sent.</param>
/// <param name="Source">Where the frame layout came from, so a reader can check it.</param>
/// <param name="Refusal">Why a recognised controller is not written to. Null when <paramref name="Writable"/> is true.</param>
public sealed record HidLightingController(
    int VendorId,
    int ProductId,
    string Name,
    string Protocol,
    bool Writable,
    string Source,
    string? Refusal = null)
{
    /// <summary>True when this entry matches a device. A zero product id matches every product of that vendor.</summary>
    public bool Matches(int vendorId, int productId) =>
        VendorId == vendorId && (ProductId == 0 || ProductId == productId);
}

/// <summary>
/// The vendor HID controllers known to exist on Acer laptops, and the rule that decides whether one may be opened.
/// </summary>
/// <remarks>
/// <para>
/// Only one controller here has a documented lighting frame: the ENE KB5130 that the 2025-era Nitro and Predator
/// boards moved their backlight onto. The protocol — an 11-byte HID feature report, <c>0xA4 0x21 mode brightness speed
/// direction R G B mask 0x00</c> — was reverse-engineered and published by the <c>kbrgb</c> project and confirmed on
/// two machines that are in this project's own model table (<c>PHN16S-71</c> and <c>ANV16S-41</c>). That is why it is
/// the one entry marked writable.
/// </para>
/// <para>
/// ITE (<c>0x048D</c>), Sonix (<c>0x0C45</c>) and Chicony (<c>0x04F2</c>) controllers do exist behind Acer keyboards,
/// and they are listed so a hardware report says which one a machine has. No frame layout for their Acer lighting is
/// published, so they are marked unwritable: inventing one would send unverified bytes to a stranger's keyboard, which
/// is the one thing this project has refused to do on every other hardware path.
/// </para>
/// <para>
/// The filtering rule is deliberately blunt. A HID interface on the generic-desktop page (<c>0x01</c>) is the keyboard
/// itself, the mouse, or a consumer-control node, and none of them may ever be opened — a write to the wrong report id
/// on a keyboard's input collection is a stuck key, not a wrong colour.
/// </para>
/// </remarks>
public static class HidLightingControllers
{
    /// <summary>Lowest usage page that is a vendor-defined page. Everything below is a standard HID page.</summary>
    public const int FirstVendorUsagePage = 0xFF00;

    /// <summary>The controllers this build recognises.</summary>
    public static IReadOnlyList<HidLightingController> All { get; } = new[]
    {
        new HidLightingController(
            VendorId: 0x0CF2,
            ProductId: 0x5130,
            Name: "ENE KB5130",
            Protocol: "ene-kb5130",
            Writable: true,
            Source: "kbrgb PROTOCOL.md (verified on PHN16S-71, confirmed on ANV16S-41)"),
        new HidLightingController(
            VendorId: 0x048D,
            ProductId: 0,
            Name: "ITE",
            Protocol: "none",
            Writable: false,
            Source: "vendor id only",
            Refusal: "no documented Acer lighting frame for this vendor"),
        new HidLightingController(
            VendorId: 0x0C45,
            ProductId: 0,
            Name: "Sonix",
            Protocol: "none",
            Writable: false,
            Source: "vendor id only",
            Refusal: "no documented Acer lighting frame for this vendor"),
        new HidLightingController(
            VendorId: 0x04F2,
            ProductId: 0,
            Name: "Chicony",
            Protocol: "none",
            Writable: false,
            Source: "NT411 reverse engineering (fixed 7-colour palette, no arbitrary RGB)",
            Refusal: "the documented Chicony controller has a fixed palette, not per-zone RGB")
    };

    /// <summary>The entry for a device, or null when nobody has described it.</summary>
    public static HidLightingController? Find(int vendorId, int productId)
    {
        // A product-specific entry wins over a vendor-wide one, so a newly documented controller is not shadowed by
        // the vendor entry that already existed.
        HidLightingController? vendorWide = null;
        foreach (var controller in All)
        {
            if (!controller.Matches(vendorId, productId)) continue;
            if (controller.ProductId != 0) return controller;
            vendorWide ??= controller;
        }

        return vendorWide;
    }

    /// <summary>
    /// True when a usage page is a standard HID page. Nothing on one of these may be opened by a lighting transport.
    /// </summary>
    public static bool IsStandardUsagePage(int usagePage) => usagePage < FirstVendorUsagePage;

    /// <summary>
    /// The decision for one interface. Kept here rather than in the Windows enumerator so the rule can be tested on a
    /// build machine with no Acer hardware.
    /// </summary>
    public static HidLightingCandidate Decide(int vendorId, int productId, int usagePage, int usage)
    {
        // The keyboard itself. Never opened, never written, whatever the vendor id says: a laptop keyboard's input
        // collection is not a lighting device, and this rule is the one thing between a colour write and a stuck key.
        if (usagePage == 0x01)
            return HidLightingCandidate.Refused(
                $"standard HID interface (page 0x{usagePage:X2} usage 0x{usage:X2}) — input collections are never opened");

        // Every other standard page is refused for the same reason, even for a writable controller: the documented
        // frame belongs to a vendor page, so a report addressed to page 0x0C is a different report to a different
        // device. Only a vendor-defined page may carry a colour.
        if (usagePage < FirstVendorUsagePage)
            return HidLightingCandidate.Refused(
                $"standard HID page 0x{usagePage:X2} (usage 0x{usage:X2}) — only vendor lighting pages are written to");

        var controller = Find(vendorId, productId);
        if (controller == null)
            return HidLightingCandidate.Refused("not a recognised Acer lighting controller");

        if (!controller.Writable)
            return HidLightingCandidate.Known(
                controller, $"0x{vendorId:X4}:0x{productId:X4}", controller.Refusal ?? "no documented frame");

        return HidLightingCandidate.ForWrite(controller, $"0x{vendorId:X4}:0x{productId:X4}");
    }
}

/// <summary>The outcome of <see cref="HidLightingControllers.Decide"/> for one interface.</summary>
/// <param name="Candidate">True when the interface belongs to a recognised Acer lighting controller.</param>
/// <param name="Writable">True when a documented frame exists and a write may be attempted.</param>
/// <param name="Controller">The matching controller, or null for an unknown device.</param>
/// <param name="Description">A short device description for the log and the report.</param>
/// <param name="Reason">Why the interface was refused, or null when it is writable.</param>
public sealed record HidLightingCandidate(
    bool Candidate,
    bool Writable,
    HidLightingController? Controller,
    string Description,
    string? Reason)
{
    public static HidLightingCandidate ForWrite(HidLightingController controller, string ids) =>
        new(true, true, controller, $"{controller.Name} {ids}", null);

    public static HidLightingCandidate Known(HidLightingController controller, string ids, string reason) =>
        new(true, false, controller, $"{controller.Name} {ids}", reason);

    public static HidLightingCandidate Refused(string reason) => new(false, false, null, "", reason);
}

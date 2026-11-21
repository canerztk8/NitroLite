using System.Globalization;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Linux;

/// <summary>What the mainline kernel's <c>acer-wmi</c> driver does for one model, and from which release.</summary>
/// <param name="ProductName">The DMI product name the driver matches, e.g. <c>Nitro AN16-41</c>.</param>
/// <param name="Since">The first kernel release whose quirk table lists the model.</param>
/// <param name="FanControlSince">The first release that also drives the fans (hwmon <c>pwm</c>), or null.</param>
/// <param name="Note">Anything a user of that model should know, in the source language.</param>
public sealed record LinuxKernelModel(string ProductName, Version Since, Version? FanControlSince, string? Note = null)
{
    public string ModelCode => ProductName[(ProductName.IndexOf(' ', StringComparison.Ordinal) + 1)..];
}

/// <summary>
/// The Nitro and Predator models the mainline kernel supports out of the box, read from the quirk table of
/// <c>drivers/platform/x86/acer-wmi.c</c> and dated against the kernel's release tags.
/// </summary>
/// <remarks>
/// <para>
/// These are facts about the kernel, not code from it: model names, release numbers and what each entry turns on. The
/// table is what lets NitroLite tell a user "Linux 7.4 supports your laptop fully; until then, turn on predator_v4"
/// instead of a generic hint, and it is what the wiki's model page is generated from.
/// </para>
/// <para>
/// Sources: the quirk table at mainline <c>master</c> (2026-11-21) and the commits that added each entry, located with
/// <c>git</c> against the <c>v6.8</c>…<c>v7.4-rc1</c> tags. The AN16-41 entry (commit <c>164de1f81266</c>) was tested
/// by its author on BIOS V1.03: five platform profiles, both fan speeds, the temperature sensors and pwm1/pwm2 in
/// custom, maximum and automatic mode.
/// </para>
/// </remarks>
public static class LinuxModelSupport
{
    public static IReadOnlyList<LinuxKernelModel> Models { get; } = new[]
    {
        new LinuxKernelModel("Nitro AN515-58", new Version(6, 14), new Version(6, 19)),
        new LinuxKernelModel("Nitro AN16-41", new Version(7, 4), new Version(7, 4)),
        new LinuxKernelModel("Nitro ANV15-51", new Version(7, 4), new Version(7, 4),
            "Bu modelin firmware'i performance profilini reddediyor; diğer dört mod çalışıyor."),
        new LinuxKernelModel("Predator PH315-53", new Version(5, 15), null,
            "Çekirdek bu modelde yalnızca Turbo tuşunu ve fan modunu destekliyor."),
        new LinuxKernelModel("Predator PHN16-71", new Version(6, 8), null),
        new LinuxKernelModel("Predator PH16-71", new Version(6, 9), null),
        new LinuxKernelModel("Predator PH18-71", new Version(6, 9), null),
        new LinuxKernelModel("Predator PH16-72", new Version(6, 14), new Version(6, 19)),
        new LinuxKernelModel("Predator PHN16-72", new Version(6, 19), new Version(6, 19)),
        new LinuxKernelModel("Predator PT14-51", new Version(6, 14), new Version(6, 19)),
    };

    /// <summary>The entry for a DMI product name or model code, or null when the kernel does not list it.</summary>
    public static LinuxKernelModel? Find(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        foreach (var model in Models)
        {
            if (string.Equals(model.ProductName, identity.Model?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(identity.ModelCode) &&
                 string.Equals(model.ModelCode, identity.BaseModelCode, StringComparison.OrdinalIgnoreCase)))
            {
                return model;
            }
        }

        return null;
    }

    /// <summary><c>7.0.0-44-generic</c> to 7.0; null when the release string does not start with two numbers.</summary>
    public static Version? ParseKernel(string? release)
    {
        if (string.IsNullOrWhiteSpace(release)) return null;

        var parts = release.Trim().Split('.', '-', '_', '+');
        if (parts.Length < 2) return null;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) &&
               int.TryParse(Digits(parts[1]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)
            ? new Version(major, minor)
            : null;
    }

    private static string Digits(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return text[..end];
    }

    /// <summary>
    /// One sentence for the settings page about this model and this kernel, or null when there is nothing to add.
    /// </summary>
    public static string? Describe(LinuxKernelModel? model, Version? kernel)
    {
        if (model == null || kernel == null) return null;

        var since = $"{model.Since.Major}.{model.Since.Minor}";
        var now = $"{kernel.Major}.{kernel.Minor}";
        if (kernel < model.Since)
        {
            return model.FanControlSince is { } fanSince
                ? Loc.T($"Acer modları: Linux {since}. Fan hızı kontrolü: Linux {fanSince}. Mevcut çekirdek: {now}. predator_v4 desteği modele bağlıdır.")
                : model.Note == null
                    ? Loc.T($"Linux {since} bu modelin Acer modlarını ve fan devrini destekliyor; şu anki çekirdek {now}. O zamana kadar predator_v4 aynı özellikleri getirir.")
                    : Loc.T(model.Note);
        }

        if (model.FanControlSince != null && kernel < model.FanControlSince)
        {
            var fan = $"{model.FanControlSince.Major}.{model.FanControlSince.Minor}";
            return Loc.T($"Çekirdeğiniz bu modelin Acer modlarını destekliyor; fan kontrolü Linux {fan} ile geliyor.");
        }

        return model.Note == null ? null : Loc.T(model.Note);
    }
}

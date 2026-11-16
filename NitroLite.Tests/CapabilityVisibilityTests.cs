using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NitroLite;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the three things this round added: the rule that only a positive "no" may hide a control, the contents of the
/// diagnostics archive, and the extra time an actionable toast gets.
/// </summary>
public class CapabilityVisibilityTests
{
    private static CapabilitySnapshot Snapshot(params HardwareProbeResult[] capabilities) => new()
    {
        ProfileId = "AN16-41",
        ProfileSource = "embedded",
        CacheKey = "AN16-41|1.27",
        Capabilities = capabilities
    };

    [Fact]
    public void IsUnsupported_IsTrueOnlyForAPositiveNo()
    {
        var snapshot = Snapshot(
            HardwareProbeResult.Unsupported(CapabilitySnapshot.MuxSwitch, "no instance"),
            HardwareProbeResult.AccessDenied(CapabilitySnapshot.WmiGamingFunction, "0x8004102F"),
            HardwareProbeResult.Unknown(CapabilitySnapshot.KeyboardZones),
            HardwareProbeResult.Supported(CapabilitySnapshot.WmiBatteryChargeLimit, "WMID_GUID5"));

        Assert.True(snapshot.IsUnsupported(CapabilitySnapshot.MuxSwitch));

        // Access denied is not a no: the probe never got to ask, and hiding on it would remove a working feature from
        // an unelevated session.
        Assert.False(snapshot.IsUnsupported(CapabilitySnapshot.WmiGamingFunction));
        Assert.False(snapshot.IsUnsupported(CapabilitySnapshot.KeyboardZones));
        Assert.False(snapshot.IsUnsupported(CapabilitySnapshot.WmiBatteryChargeLimit));
    }

    [Fact]
    public void IsUnsupported_IsFalseForACapabilityThatWasNeverProbed()
    {
        var snapshot = Snapshot(HardwareProbeResult.Supported(CapabilitySnapshot.MuxSwitch));

        Assert.False(snapshot.IsUnsupported(CapabilitySnapshot.KeyboardZones));
        Assert.False(snapshot.IsUnsupported("something.else"));
    }

    [Fact]
    public void IsSupportedAndIsUnsupported_AreNeverBothTrue()
    {
        foreach (var outcome in Enum.GetValues<HardwareProbeOutcome>())
        {
            var snapshot = Snapshot(new HardwareProbeResult { Capability = "x", Outcome = outcome });
            Assert.False(snapshot.IsSupported("x") && snapshot.IsUnsupported("x"));
        }
    }

    [Fact]
    public void EveryCapabilityConstantHasATranslatableLabel()
    {
        var constants = new[]
        {
            CapabilitySnapshot.WmiGamingFunction,
            CapabilitySnapshot.WmiBatteryChargeLimit,
            CapabilitySnapshot.MuxSwitch,
            CapabilitySnapshot.KeyboardZones,
            CapabilitySnapshot.GpuOverclock,
            CapabilitySnapshot.PanelOverdrive,
            CapabilitySnapshot.ProfileMatch
        };

        foreach (var capability in constants)
        {
            var label = MainWindow.CapabilityLabel(capability);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(capability, label);
        }
    }

    [Fact]
    public void AnUnknownCapabilityFallsBackToItsKeyRatherThanAnEmptyLabel()
    {
        Assert.Equal("brand.new", MainWindow.CapabilityLabel("brand.new"));
    }

    [Fact]
    public void EveryProbeOutcomeHasAVerdict()
    {
        foreach (var outcome in Enum.GetValues<HardwareProbeOutcome>())
        {
            var verdict = MainWindow.VerdictLabel(outcome);
            Assert.False(string.IsNullOrWhiteSpace(verdict));
        }
    }

    [Fact]
    public void TheWalkthroughHasThreeSteps()
    {
        Assert.Equal(3, MainWindow.OnboardingStepCount);
    }

    // ---------------------------------------------------------------- diagnostics bundle

    [Fact]
    public void BuildEntries_CarriesTheFourAlwaysPresentFiles()
    {
        var entries = DiagnosticsBundle.BuildEntries(
            Snapshot(HardwareProbeResult.Supported(CapabilitySnapshot.MuxSwitch)),
            new DeviceCapabilities { GpuModeCapability = 2 },
            "{\"a\":1}",
            "log line");

        var names = entries.Select(e => e.Name).ToList();
        Assert.Contains("system.txt", names);
        Assert.Contains("hardware-report.txt", names);
        Assert.Contains("capabilities.json", names);
        Assert.Contains(DiagnosticsBundle.LogFileName, names);
        Assert.Contains(DiagnosticsBundle.SettingsFileName, names);
    }

    [Fact]
    public void BuildEntries_LeavesOutAMissingLogAndSettingsFile()
    {
        var entries = DiagnosticsBundle.BuildEntries(null, null, null, null);
        var names = entries.Select(e => e.Name).ToList();

        // An empty placeholder would make the archive look complete when it is not.
        Assert.DoesNotContain(DiagnosticsBundle.LogFileName, names);
        Assert.DoesNotContain(DiagnosticsBundle.SettingsFileName, names);
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void BuildEntries_SystemTextNamesTheProfileAndTheCapabilities()
    {
        var entries = DiagnosticsBundle.BuildEntries(
            Snapshot(HardwareProbeResult.Unsupported(CapabilitySnapshot.MuxSwitch, "no instance")));
        var system = entries.Single(e => e.Name == "system.txt").Content;

        Assert.Contains("AN16-41", system);
        Assert.Contains(CapabilitySnapshot.MuxSwitch, system);
        Assert.Contains("not supported", system);
    }

    [Fact]
    public void BuildEntries_SystemTextSaysSoWhenNoSnapshotExists()
    {
        var entries = DiagnosticsBundle.BuildEntries();
        var system = entries.Single(e => e.Name == "system.txt").Content;

        Assert.Contains("No capability snapshot", system);
    }

    [Fact]
    public void BuildCapabilitiesJson_IsValidJsonForANullSnapshot()
    {
        var json = DiagnosticsBundle.BuildCapabilitiesJson(null, null);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("snapshot").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("live").ValueKind);
    }

    [Fact]
    public void BuildCapabilitiesJson_IsValidJsonWithASnapshotAndLiveCapabilities()
    {
        var json = DiagnosticsBundle.BuildCapabilitiesJson(
            Snapshot(HardwareProbeResult.Supported(CapabilitySnapshot.MuxSwitch, "0x0102")),
            new DeviceCapabilities
            {
                GpuModeCapability = 2,
                FanMode = FanMode.Auto,
                OperatingModes = new[] { OperatingMode.Default, OperatingMode.Turbo }
            });

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var snapshot = document.RootElement.GetProperty("snapshot");
        Assert.Equal("AN16-41", snapshot.GetProperty("profileId").GetString());
        Assert.Equal(1, snapshot.GetProperty("capabilities").GetArrayLength());

        var live = document.RootElement.GetProperty("live");
        Assert.Equal(2, live.GetProperty("gpuModeCapability").GetInt32());
        Assert.Equal(2, live.GetProperty("operatingModes").GetArrayLength());
    }

    [Fact]
    public void BuildCapabilitiesJson_EscapesQuotesInADetail()
    {
        var json = DiagnosticsBundle.BuildCapabilitiesJson(
            Snapshot(HardwareProbeResult.Failed("x", "said \"no\"")), null);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("said \"no\"",
            document.RootElement.GetProperty("snapshot").GetProperty("capabilities")[0].GetProperty("detail").GetString());
    }

    [Fact]
    public void Create_WritesAZipWithTheEntriesInside()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nitrolite-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var logPath = Path.Combine(directory, "nitrolite.log");
            Directory.CreateDirectory(directory);
            File.WriteAllText(settingsPath, "{\"theme\":\"dark\"}");
            File.WriteAllText(logPath, "INFO started");

            var path = DiagnosticsBundle.Create(
                directory,
                Snapshot(HardwareProbeResult.Supported(CapabilitySnapshot.MuxSwitch)),
                null,
                settingsPath,
                logPath);

            Assert.True(File.Exists(path));
            Assert.StartsWith(DiagnosticsBundle.FileNamePrefix, Path.GetFileName(path));

            using var archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("system.txt", names);
            Assert.Contains("hardware-report.txt", names);
            Assert.Contains("capabilities.json", names);
            Assert.Contains(DiagnosticsBundle.LogFileName, names);
            Assert.Contains(DiagnosticsBundle.SettingsFileName, names);

            using var reader = new StreamReader(archive.GetEntry(DiagnosticsBundle.LogFileName)!.Open());
            Assert.Equal("INFO started", reader.ReadToEnd());
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public void Create_DoesNotOverwriteAnEarlierArchiveFromTheSameSecond()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nitrolite-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
            var first = DiagnosticsBundle.Create(directory, null, null, null, null, stamp);

            // The user may already have attached the first one to a report, so a second export must not replace it.
            Assert.ThrowsAny<IOException>(() => DiagnosticsBundle.Create(directory, null, null, null, null, stamp));
            Assert.True(File.Exists(first));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- toast action

    [Fact]
    public void AnActionableToastStaysUpLongerThanAPlainOne()
    {
        const string message = "Kısa bir hata";

        Assert.True(MainWindow.ToastDurationFor(message, hasAction: true) > MainWindow.ToastDurationFor(message));
    }

    [Fact]
    public void AnActionableToastIsCappedSoItCannotSitOnTheScreen()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), MainWindow.ToastDurationFor(new string('a', 4000), hasAction: true));
    }

    [Fact]
    public void AnActionableToastNeverFallsBelowItsFloor()
    {
        for (int length = 0; length <= 400; length += 7)
        {
            var duration = MainWindow.ToastDurationFor(new string('a', length), hasAction: true);
            Assert.InRange(duration.TotalSeconds, 8, 15);
        }
    }
}

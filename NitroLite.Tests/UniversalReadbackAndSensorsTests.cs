using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Verification;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the universal read-back rule and the sensor fallback chain.
/// <para>
/// Both were written from the AN16-61 (BIOS 1.53) report: a MUX toggle that said "applied" while nothing changed, a
/// charge limit that reported success while the battery kept charging, a Windows-key lock with no read-back at all, and
/// "0 MHz" for a CPU clock because AASSvc never answered that field. The tests below are the four shapes of that
/// report — the write is confirmed, the write is contradicted, the write cannot be read back, and the reading has to
/// come from a lower layer.
/// </para>
/// </summary>
public class UniversalReadbackAndSensorsTests
{
    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    // ---------------------------------------------------------------- session verification

    [Fact]
    public void Verification_MismatchIsRecordedAndNeedsAttention()
    {
        var verification = new SessionVerification();

        verification.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.MuxMode,
            Verdict = ControlVerdict.Mismatch,
            Requested = "DiscreteGpu",
            Observed = "0x0200",
            Detail = "the firmware kept the hybrid mode"
        });

        Assert.True(verification.HasUnverified);
        Assert.Equal(1, verification.UnverifiedCount);
        Assert.Equal(ControlVerdict.Mismatch, verification.For(ReadBackPolicy.MuxMode)!.Verdict);

        // The hardware report is where this ends up, so the line has to name the control and both values.
        var description = verification.Describe();
        Assert.Contains("MUX switch", description, StringComparison.Ordinal);
        Assert.Contains("DiscreteGpu", description, StringComparison.Ordinal);
        Assert.Contains("0x0200", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_ConfirmingAControlClearsItsNote()
    {
        var verification = new SessionVerification();

        verification.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.BatteryLimit,
            Verdict = ControlVerdict.Unverifiable,
            Requested = "Optimized80"
        });

        Assert.True(verification.HasUnverified);

        verification.Confirm(ReadBackPolicy.BatteryLimit);

        Assert.False(verification.HasUnverified);
        Assert.Null(verification.For(ReadBackPolicy.BatteryLimit));
        Assert.Equal("", verification.Describe());
    }

    [Fact]
    public void Verification_ARefusedTransportIsRecordedButDoesNotAskForAReport()
    {
        var verification = new SessionVerification();

        verification.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.WinKeyLock,
            Verdict = ControlVerdict.TransportMissing,
            Detail = "the elevated worker is not running"
        });

        // A machine without a supervisor is not a compatibility bug: it says nothing about the hardware, so it must
        // not put a "send us a report" banner in front of a user who simply declined the UAC prompt.
        Assert.False(verification.HasUnverified);
        Assert.Equal(ControlVerdict.TransportMissing, verification.For(ReadBackPolicy.WinKeyLock)!.Verdict);
        Assert.Equal("", verification.Describe());
    }

    [Fact]
    public void Verification_OneNotePerControl()
    {
        var verification = new SessionVerification();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            verification.Mark(new VerificationNote
            {
                Control = ReadBackPolicy.MuxMode,
                Verdict = ControlVerdict.Mismatch,
                Detail = $"attempt {attempt}"
            });
        }

        Assert.Equal(1, verification.UnverifiedCount);
        Assert.Contains("attempt 2", verification.Describe(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- read-back policy

    [Fact]
    public async Task ReadBack_MismatchRevertsTheControlAndIsNotedForTheSession()
    {
        var settings = new AppSettings();
        var verification = new SessionVerification();
        var policy = new ReadBackPolicy(settings, NoDelay, verification);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.MuxMode,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus),
            matches: observed => observed == GpuOperatingMode.DiscreteGpu,
            requested: GpuOperatingMode.DiscreteGpu.ToString());

        // The caller reverts its toggle on a false answer; the note is what makes the reason visible afterwards.
        Assert.False(result.Applied);
        Assert.False(result.Verified);
        Assert.Equal(ReadBackPolicy.MuxMode, result.Control);
        Assert.Equal(GpuOperatingMode.Optimus.ToString(), result.Observed);
        Assert.True(result.DisabledForSession);

        var note = verification.For(ReadBackPolicy.MuxMode);
        Assert.NotNull(note);
        Assert.Equal(ControlVerdict.Mismatch, note!.Verdict);
        Assert.Equal(GpuOperatingMode.DiscreteGpu.ToString(), note.Requested);
        Assert.Equal(GpuOperatingMode.Optimus.ToString(), note.Observed);
    }

    [Fact]
    public async Task ReadBack_ASuccessfulReadBackConfirmsTheControl()
    {
        var settings = new AppSettings();
        var verification = new SessionVerification();
        var policy = new ReadBackPolicy(settings, NoDelay, verification);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.BatteryLimit,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Optimized80),
            matches: observed => observed == BatteryChargeMode.Optimized80);

        Assert.True(result.Applied);
        Assert.True(result.Verified);
        Assert.False(verification.HasUnverified);
    }

    [Fact]
    public async Task ReadBack_ANoTransportWriteIsNotCountedAgainstTheFirmware()
    {
        var settings = new AppSettings();
        var verification = new SessionVerification();
        var policy = new ReadBackPolicy(settings, NoDelay, verification);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.BatteryLimit,
            write: () => throw new WmiTransportUnavailableException("the worker is not running"),
            readBack: () => Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Full),
            matches: observed => observed == BatteryChargeMode.Optimized80);

        Assert.True(result.TransportMissing);
        Assert.False(result.Applied);
        Assert.False(policy.IsDisabled(ReadBackPolicy.BatteryLimit));
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.BatteryLimit));
        Assert.False(verification.HasUnverified);
    }

    [Fact]
    public async Task ReadBack_ASettingWithNoGetterIsUnverifiableAndStaysEnabled()
    {
        var settings = new AppSettings();
        var verification = new SessionVerification();
        var policy = new ReadBackPolicy(settings, NoDelay, verification);

        // The Windows-key lock on every Acer firmware we have seen: SetGamingProfile accepts it, and no getter answers
        // the state. Reading the overdrive word's bit 24 as "the lock is on" would be inventing a measurement.
        var result = await policy.ApplyUnverifiableAsync(
            ReadBackPolicy.WinKeyLock,
            requested: "enabled",
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<bool?>(null),
            matches: observed => observed);

        Assert.False(result.Applied);
        Assert.True(result.Unverifiable);
        Assert.False(result.DisabledForSession);
        Assert.False(policy.IsDisabled(ReadBackPolicy.WinKeyLock));

        var note = verification.For(ReadBackPolicy.WinKeyLock);
        Assert.NotNull(note);
        Assert.Equal(ControlVerdict.Unverifiable, note!.Verdict);
        Assert.True(note.NeedsAttention);
    }

    [Fact]
    public async Task ReadBack_ASettingWithAGetterStillRevertsOnMismatch()
    {
        var settings = new AppSettings();
        var verification = new SessionVerification();
        var policy = new ReadBackPolicy(settings, NoDelay, verification);

        // A firmware that grew a getter (or the Care Center path that has one) goes through the same rule as the
        // switch: the value read back wins, and the caller has to revert.
        var result = await policy.ApplyUnverifiableAsync(
            ReadBackPolicy.WinKeyLock,
            requested: "enabled",
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<bool?>(false),
            matches: observed => observed);

        Assert.False(result.Applied);
        Assert.False(result.Unverifiable);
        Assert.True(result.DisabledForSession);
        Assert.Equal(ControlVerdict.Mismatch, verification.For(ReadBackPolicy.WinKeyLock)!.Verdict);
    }

    [Fact]
    public async Task ReadBack_TheSettleDelayPrecedesTheFirstRead()
    {
        var settings = new AppSettings();
        var delays = new List<TimeSpan>();
        var policy = new ReadBackPolicy(settings, interval =>
        {
            delays.Add(interval);
            return Task.CompletedTask;
        });

        await policy.ApplyAsync(
            ReadBackPolicy.BatteryLimit,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Full),
            matches: observed => observed == BatteryChargeMode.Full);

        // 320 ms before the first read: the charge limit is applied by an agent, and the firmware answers the previous
        // value for a moment after accepting the command.
        Assert.Equal(new[] { ReadBackPolicy.SettleDelay }, delays);
        Assert.True(ReadBackPolicy.SettleDelay > ReadBackPolicy.RetryDelay);
    }

    // ---------------------------------------------------------------- view model

    [Fact]
    public async Task ViewModel_ARefusedChargeLimitRevertsToWhatTheMachineReports()
    {
        var hardware = new Phase3Fakes.StubHardware { BatteryMode = BatteryChargeMode.Full, BatteryWriteSucceeds = false };
        using var vm = Create(hardware, out _);

        await vm.InitializeAsync();
        Assert.False(vm.BatteryLimitOn);

        vm.BatteryLimitOn = true;

        await Phase3Fakes.WaitUntilAsync(() => hardware.Writes.Contains("charge:" + BatteryChargeMode.Optimized80));
        await Phase3Fakes.WaitUntilAsync(() => !vm.BatteryLimitOn);

        Assert.False(vm.BatteryLimitOn);
    }

    [Fact]
    public async Task ViewModel_AnAcceptedChargeLimitKeepsTheSwitchOn()
    {
        var hardware = new Phase3Fakes.StubHardware { BatteryMode = BatteryChargeMode.Full };
        using var vm = Create(hardware, out _);

        await vm.InitializeAsync();
        vm.BatteryLimitOn = true;

        await Phase3Fakes.WaitUntilAsync(() => hardware.BatteryMode == BatteryChargeMode.Optimized80);

        Assert.True(vm.BatteryLimitOn);
    }

    [Fact]
    public async Task ViewModel_TheHintAppearsOnlyWhenAControlCouldNotBeConfirmed()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = Create(hardware, out var verification);

        await vm.InitializeAsync();
        Assert.False(vm.HasVerificationHint);
        Assert.Equal("", vm.VerificationHintText);

        verification.Mark(new VerificationNote
        {
            Control = ReadBackPolicy.MuxMode,
            Verdict = ControlVerdict.Mismatch,
            Requested = "DiscreteGpu",
            Observed = "Optimus"
        });

        // The hint is refreshed by the write path, so a test that only marks a note has to ask the view model to
        // reread it the same way the next write would.
        hardware.Writes.Add("refresh");
        vm.BatteryLimitOn = true;
        await Phase3Fakes.WaitUntilAsync(() => hardware.BatteryMode == BatteryChargeMode.Optimized80);

        Assert.True(vm.HasVerificationHint);
        Assert.Contains("MUX switch", vm.VerificationHintText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- telemetry fallback

    [Fact]
    public void Telemetry_TheMonitorRecordsWhereEveryReadingCameFrom()
    {
        using var monitor = new HardwareMonitorService();

        // No firmware answered for this sample: every reading has to come from a Windows layer or be reported as
        // unavailable, and never as a zero that looks like a measurement.
        var data = monitor.CreateFallbackMonitorData();
        var provenance = data.Provenance;

        Assert.True(provenance.ContainsKey("cpu-temp"));
        Assert.True(provenance.ContainsKey("cpu-clock"));
        Assert.True(provenance.ContainsKey("cpu-power"));
        Assert.Equal("estimate", provenance["cpu-power"]);

        // The GPU readings cannot be a firmware value here: nothing Acer-shaped answered.
        Assert.True(provenance.ContainsKey("gpu-temp"));
        Assert.DoesNotContain("firmware", provenance["gpu-temp"], StringComparison.Ordinal);
    }

    private static MainViewModel Create(Phase3Fakes.StubHardware hardware, out SessionVerification verification)
    {
        var ledger = new SessionVerification();
        verification = ledger;

        return new MainViewModel(
            new AppSettings(),
            hardware,
            null,
            Phase3Fakes.TempSettingsPath(),
            verification: ledger);
    }
}

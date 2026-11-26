using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.Mvvm;
using NitroLite.Desktop.ViewModels;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The MUX row's honesty pass: the third mode, the restart note, and the refusal that used to read as a success.
/// </summary>
/// <remarks>
/// Two things were wrong and both were silent. The service backend sent whatever mode it was handed and took the
/// reply at face value, so a mode the firmware does not carry still printed "Uygulandı". And the card set its pending
/// mode from the selection rather than from the write, so a refused switch still raised the restart badge. The tests
/// below keep the two apart: a staged write says "restart", a refused one says why.
/// </remarks>
public class Wave5GpuModeHonestyTests
{
    private static MainViewModel Create(AppSettings settings, IHardwareController hardware, string path) =>
        new(settings, hardware, null, path);

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a leftover temp file is not a failure */ }
    }

    private static async Task RunAsync(AsyncCommand command)
    {
        command.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => command.CanExecute(null));
    }

    // ---------------------------------------------------------------- the service backend's guard

    /// <summary>An agent that answers from memory and records what it was asked to write.</summary>
    private sealed class FakeAgent : AcerAgentClient
    {
        public FakeAgent()
            : base("127.0.0.1", DeadEndService.Port, DeadEndService.Budget, DeadEndService.Budget, DeadEndService.Budget,
                registry: () => NitroLite.Core.Hardware.AcerRegistrySnapshot.Empty)
        {
        }

        public int? Capability { get; set; }

        public List<GpuOperatingMode> Writes { get; } = new();

        public override bool IsConnected => true;

        public override Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public override Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult(Capability);

        public override Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
        {
            Writes.Add(mode);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task AgentBackend_RefusesAModeTheBitmaskDoesNotAdvertise()
    {
        // 3 = Optimus and discrete only. The service would still answer "success" to Automatic, which is exactly how
        // a mode the machine does not carry came back as applied.
        var agent = new FakeAgent { Capability = 3 };
        using var controller = new AgentHardwareController(agent, new CareCenterClient());

        Assert.False(await controller.SetGpuModeAsync(GpuOperatingMode.Automatic));
        Assert.Empty(agent.Writes);
    }

    [Fact]
    public async Task AgentBackend_WritesAModeTheBitmaskAdvertises()
    {
        // 7 is what an AN16-41 reports: all three modes, automatic included.
        var agent = new FakeAgent { Capability = 7 };
        using var controller = new AgentHardwareController(agent, new CareCenterClient());

        Assert.True(await controller.SetGpuModeAsync(GpuOperatingMode.Automatic));
        Assert.Equal(new[] { GpuOperatingMode.Automatic }, agent.Writes);
    }

    [Fact]
    public async Task AgentBackend_WithoutAMask_SendsTheWriteRatherThanGuessing()
    {
        // No answer is not evidence of no support: the service may simply not carry the capability packet.
        var agent = new FakeAgent { Capability = null };
        using var controller = new AgentHardwareController(agent, new CareCenterClient());

        Assert.True(await controller.SetGpuModeAsync(GpuOperatingMode.DiscreteGpu));
        Assert.Equal(new[] { GpuOperatingMode.DiscreteGpu }, agent.Writes);
    }

    // ---------------------------------------------------------------- the card's note

    [Fact]
    public async Task ApplyGpuMode_StagedAutomatic_SaysARestartIsNeeded()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = Create(new AppSettings(), hardware, path);
            await vm.InitializeAsync();

            vm.SelectedGpuMode = vm.GpuModes[2];
            await RunAsync(vm.ApplyGpuModeCommand);

            Assert.Equal(GpuOperatingMode.Automatic, vm.PendingGpuMode);
            Assert.True(vm.ShowPendingGpuModeBadge);
            Assert.True(vm.HasGpuModeNote);
            Assert.Equal(vm.Labels.GpuPendingAutomatic, vm.GpuModeNote);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ApplyGpuMode_RefusedAutomatic_ExplainsInsteadOfAskingForARestart()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware { GpuWriteResult = false };
            using var vm = Create(new AppSettings(), hardware, path);
            await vm.InitializeAsync();

            vm.SelectedGpuMode = vm.GpuModes[2];
            await RunAsync(vm.ApplyGpuModeCommand);

            // Nothing was staged, so there is nothing to restart for.
            Assert.Null(vm.PendingGpuMode);
            Assert.False(vm.ShowPendingGpuModeBadge);
            Assert.True(vm.HasGpuModeNote);
            Assert.Equal(vm.Labels.GpuAutomaticNotWritable, vm.GpuModeNote);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ApplyGpuMode_RefusedDiscrete_LeavesTheRowQuiet()
    {
        // The status line already says the write failed. A second sentence would only repeat it.
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware { GpuWriteResult = false };
            using var vm = Create(new AppSettings(), hardware, path);
            await vm.InitializeAsync();

            vm.SelectedGpuMode = vm.GpuModes[1];
            await RunAsync(vm.ApplyGpuModeCommand);

            Assert.Null(vm.PendingGpuMode);
            Assert.False(vm.HasGpuModeNote);
            Assert.Null(vm.SelectedGpuMode); // The fake has no current-mode read-back.
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ApplyGpuMode_StagedDiscrete_KeepsTheExistingRestartSentence()
    {
        var path = Phase3Fakes.TempSettingsPath();
        try
        {
            var hardware = new Phase3Fakes.StubHardware();
            using var vm = Create(new AppSettings(), hardware, path);
            await vm.InitializeAsync();

            vm.SelectedGpuMode = vm.GpuModes[1];
            await RunAsync(vm.ApplyGpuModeCommand);

            Assert.Equal(GpuOperatingMode.DiscreteGpu, vm.PendingGpuMode);
            Assert.Equal(vm.Labels.GpuPendingDiscrete, vm.GpuModeNote);

            // "Uygulandı" is a claim the firmware has not made yet: it reads the setting at POST.
            Assert.Equal(vm.Labels.GpuModeStaged, vm.Status);
        }
        finally
        {
            Delete(path);
        }
    }
}

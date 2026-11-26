using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the third step of the hardware-layer migration: the byte-level WMI protocol, the framing and handshake of
/// the worker pipe, the worker's own argument checks, the job object that ties the worker's life to the window, and
/// the parent watchdog that covers the elevated case the job object cannot.
/// <para>
/// The pipe tests use a real <see cref="System.IO.Pipes.NamedPipeServerStream"/> with
/// <see cref="System.IO.Pipes.PipeOptions.CurrentUserOnly"/> and a fake executor, so the ACL, the framing and the
/// command whitelist are all exercised end to end without touching the hardware.
/// </para>
/// </summary>
public class WmiWorkerTests
{
    // ---------------------------------------------------------------- protocol encoding

    [Fact]
    public void EncodeFanSpeed_CpuSixty_Is0x3C01()
    {
        Assert.Equal(0x3C01UL, AcerWmiProtocol.EncodeFanSpeed(AcerWmiProtocol.FanIdCpu, 60));
    }

    [Fact]
    public void EncodeFanSpeed_GpuSixty_Is0x3C04()
    {
        Assert.Equal(0x3C04UL, AcerWmiProtocol.EncodeFanSpeed(AcerWmiProtocol.FanIdGpu, 60));
    }

    [Fact]
    public void EncodeFanBehavior_BothCustom_Is0x00C30009()
    {
        var encoded = AcerWmiProtocol.EncodeFanBehavior(
            AcerWmiProtocol.FanBitmapBoth, FanMode.Custom, FanMode.Custom);

        Assert.Equal(0x00C30009UL, encoded);
    }

    [Fact]
    public void FanBitmapGpu_IsBit3()
    {
        // BIT(3), not BIT(2). Measured on an AN16-41: GetGamingFanBehavior(0x08) answered 0x00004000 with the GPU
        // mode in bits 15:14, while 0x04 selected nothing.
        Assert.Equal(0x08, AcerWmiProtocol.FanBitmapGpu);
        Assert.Equal(0x01, AcerWmiProtocol.FanBitmapCpu);
        Assert.Equal(0x09, AcerWmiProtocol.FanBitmapBoth);
        Assert.Equal(AcerWmiProtocol.FanBitmapCpu | AcerWmiProtocol.FanBitmapGpu, AcerWmiProtocol.FanBitmapBoth);
    }

    [Fact]
    public void DecodeFanBehavior_RealAnswers_UseTheGetterBitPositions()
    {
        // Measured on an AN16-41. The getter answers the CPU mode in bits 9:8 and the GPU mode in bits 15:14, which
        // is not where the setter puts them (17:16 and 23:22).
        var cpuOnly = AcerWmiProtocol.DecodeFanBehavior(0x00000100);
        Assert.Equal(AcerWmiProtocol.StatusOk, cpuOnly.Status);
        Assert.Equal(FanMode.Auto, cpuOnly.Cpu);

        var gpuOnly = AcerWmiProtocol.DecodeFanBehavior(0x00004000);
        Assert.Equal(FanMode.Auto, gpuOnly.Gpu);

        var both = AcerWmiProtocol.DecodeFanBehavior(0x00004100);
        Assert.Equal(AcerWmiProtocol.StatusOk, both.Status);
        Assert.Equal(FanMode.Auto, both.Cpu);
        Assert.Equal(FanMode.Auto, both.Gpu);
    }

    [Fact]
    public void DecodeFanBehavior_ReadsTurboAndCustom()
    {
        // Turbo is 2 and Custom is 3, in the same two fields: bits 9:8 for the CPU fan, bits 15:14 for the GPU fan.
        var turbo = AcerWmiProtocol.DecodeFanBehavior(0x00008200);
        Assert.Equal(FanMode.Max, turbo.Cpu);
        Assert.Equal(FanMode.Max, turbo.Gpu);

        var custom = AcerWmiProtocol.DecodeFanBehavior(0x0000C300);
        Assert.Equal(FanMode.Custom, custom.Cpu);
        Assert.Equal(FanMode.Custom, custom.Gpu);
    }

    [Fact]
    public void DecodeFanBehavior_ZeroBitmapAnswer_IsAStatusError()
    {
        // GetGamingFanBehavior(0x00) answered 0x00000002: status 2, no fields. The low byte is the status, not a
        // bitmap, so a caller must not read it as one.
        var answer = AcerWmiProtocol.DecodeFanBehavior(0x00000002);

        Assert.Equal(0x02, answer.Status);
        Assert.NotEqual(AcerWmiProtocol.StatusOk, answer.Status);
    }

    [Fact]
    public void EncodeFanBehavior_SetterLayout_IsUnchanged()
    {
        // The setter keeps the CPU mode in bits 17:16 and the GPU mode in bits 23:22. Only the getter is asymmetric.
        Assert.Equal(0x00410009UL, AcerWmiProtocol.EncodeFanBehavior(
            AcerWmiProtocol.FanBitmapBoth, FanMode.Auto, FanMode.Auto));
        Assert.Equal(0x00820009UL, AcerWmiProtocol.EncodeFanBehavior(
            AcerWmiProtocol.FanBitmapBoth, FanMode.Max, FanMode.Max));
    }

    [Fact]
    public void EncodeOperatingMode_Extreme_Is0x040B()
    {
        Assert.Equal(0x040BUL, AcerWmiProtocol.EncodeOperatingMode(OperatingMode.Extreme));
    }

    [Fact]
    public void EncodeMuxMode_Discrete_Is0x0102()
    {
        // The firmware numbers the GPU modes from one and 1 is discrete only. Measured on an AN16-41 sitting in
        // hybrid mode, where GetGamingMiscSetting(0x02) answered 0x0202.
        Assert.Equal(0x0102UL, AcerWmiProtocol.EncodeMuxMode(GpuOperatingMode.DiscreteGpu));
    }

    [Fact]
    public void EncodeMuxMode_Optimus_Is0x0202()
    {
        Assert.Equal(0x0202UL, AcerWmiProtocol.EncodeMuxMode(GpuOperatingMode.Optimus));
    }

    [Fact]
    public void EncodeMuxMode_Automatic_Is0x0302()
    {
        Assert.Equal(0x0302UL, AcerWmiProtocol.EncodeMuxMode(GpuOperatingMode.Automatic));
    }

    [Fact]
    public void EncodeMuxMode_UnknownMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AcerWmiProtocol.EncodeMuxMode((GpuOperatingMode)99));
    }

    [Fact]
    public void DecodeFanSpeed_RealAnswer_ReadsPercentAndStatus()
    {
        // 8704 is what GetGamingFanSpeed(0x01) actually returned on an AN16-41: 0x2200 -> 34 %, status 0.
        const ulong answer = 8704;

        Assert.Equal(34, AcerWmiProtocol.DecodeFanPercent(answer));
        Assert.Equal(AcerWmiProtocol.StatusOk, AcerWmiProtocol.DecodeStatus(answer));
    }

    [Fact]
    public void DecodeStatus_NonZeroLowByte_IsAnError()
    {
        Assert.Equal(0x01, AcerWmiProtocol.DecodeStatus(0x2201));
        Assert.NotEqual(AcerWmiProtocol.StatusOk, AcerWmiProtocol.DecodeStatus(0x2201));
    }

    [Fact]
    public void DecodeOperatingMode_RealAnswer_IsDefault()
    {
        // GetGamingMiscSetting(0x0B) returned 256 = 0x0100 on an AN16-41: mode 1, status 0.
        Assert.Equal(OperatingMode.Default, AcerWmiProtocol.DecodeOperatingMode(256));
        Assert.Null(AcerWmiProtocol.DecodeOperatingMode(0x0101));
    }

    [Fact]
    public void DecodeMuxMode_ReadsBothSwitchableModes()
    {
        // 1 is discrete only and 2 is hybrid / Optimus. The earlier decoder had these the other way round.
        Assert.Equal(GpuOperatingMode.DiscreteGpu, AcerWmiProtocol.DecodeMuxMode(0x0102));
        Assert.Equal(GpuOperatingMode.Optimus, AcerWmiProtocol.DecodeMuxMode(0x0202));
        Assert.Equal(GpuOperatingMode.Automatic, AcerWmiProtocol.DecodeMuxMode(0x0302));
        Assert.Null(AcerWmiProtocol.DecodeMuxMode(0x0002));
        Assert.Null(AcerWmiProtocol.DecodeMuxMode(0x0402));
    }

    [Fact]
    public void DecodeMuxMode_RealHybridAnswer_IsOptimus()
    {
        // GetGamingMiscSetting(0x02) on an AN16-41 in hybrid mode answered 0x00000200.
        Assert.Equal(GpuOperatingMode.Optimus, AcerWmiProtocol.DecodeMuxMode(0x00000200));
    }

    // ---------------------------------------------------------------- sensors

    [Fact]
    public void EncodeSysInfoQuery_NamesOneSensor()
    {
        // 1 | (sensor << 8). Measured on an AN16-41: 0x0101 answered 0x00003F00 (63 C) and 0x0201 answered
        // 0x00098400 (2436 RPM).
        Assert.Equal(0x0101U, AcerWmiProtocol.EncodeSysInfoQuery(AcerWmiProtocol.SensorCpuTemperature));
        Assert.Equal(0x0201U, AcerWmiProtocol.EncodeSysInfoQuery(AcerWmiProtocol.SensorCpuFanSpeed));
        Assert.Equal(0x0301U, AcerWmiProtocol.EncodeSysInfoQuery(AcerWmiProtocol.SensorSystemTemperature));
        Assert.Equal(0x0601U, AcerWmiProtocol.EncodeSysInfoQuery(AcerWmiProtocol.SensorGpuFanSpeed));
        Assert.Equal(0x0A01U, AcerWmiProtocol.EncodeSysInfoQuery(AcerWmiProtocol.SensorGpuTemperature));
    }

    [Fact]
    public void DecodeSysInfoValue_RealAnswers()
    {
        // The five answers the AN16-41 gave, next to what the Acer service reported at the same moment.
        Assert.Equal(63, AcerWmiProtocol.DecodeSysInfoValue(0x00003F00));
        Assert.Equal(2436, AcerWmiProtocol.DecodeSysInfoValue(0x00098400));
        Assert.Equal(55, AcerWmiProtocol.DecodeSysInfoValue(0x00003700));
        Assert.Equal(2409, AcerWmiProtocol.DecodeSysInfoValue(0x00096900));
        Assert.Equal(57, AcerWmiProtocol.DecodeSysInfoValue(0x00003900));
    }

    [Fact]
    public void DecodeSysInfoValue_IgnoresTheStatusByte()
    {
        // A status-1 answer carries no reading; the decoder still reports the field, and the caller gates on status.
        Assert.Equal(0, AcerWmiProtocol.DecodeSysInfoValue(0x00000001));
        Assert.Equal(0x01, AcerWmiProtocol.DecodeStatus(0x00000001));
    }

    [Fact]
    public void SensorQuery_Default_IsTheFirmwareEnum()
    {
        var query = SensorQuery.Default;

        Assert.Equal(1, query.CpuTemp);
        Assert.Equal(2, query.CpuFanRpm);
        Assert.Equal(3, query.Sys1Temp);
        Assert.Equal(6, query.GpuFanRpm);
        Assert.Equal(10, query.GpuTemp);
    }

    [Fact]
    public void SensorQuery_FromLayout_CarriesTheProfileIds()
    {
        var layout = new SensorLayout
        {
            CpuTempSensor = 11,
            CpuFanRpmSensor = 12,
            Sys1TempSensor = 13,
            GpuFanRpmSensor = 14,
            GpuTempSensor = 15
        };

        var query = SensorQuery.FromLayout(layout);

        Assert.Equal(11, query.CpuTemp);
        Assert.Equal(12, query.CpuFanRpm);
        Assert.Equal(13, query.Sys1Temp);
        Assert.Equal(14, query.GpuFanRpm);
        Assert.Equal(15, query.GpuTemp);
    }

    // ---------------------------------------------------------------- worker-side validation

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(100, true)]
    [InlineData(15, false)]
    [InlineData(29, false)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    public void IsValidFanPercent_RejectsBelowTheStallFloor(int percent, bool expected)
    {
        Assert.Equal(expected, WmiWorkerValidation.IsValidFanPercent(percent));
    }

    [Fact]
    public void TryParseMuxMode_RejectsAutomatic()
    {
        Assert.False(WmiWorkerValidation.TryParseMuxMode((int)GpuOperatingMode.Automatic, out _));
        Assert.True(WmiWorkerValidation.TryParseMuxMode((int)GpuOperatingMode.Optimus, out var optimus));
        Assert.Equal(GpuOperatingMode.Optimus, optimus);
    }

    [Fact]
    public void TryParseOperatingMode_RejectsTheGapsInTheEnum()
    {
        // 2 and 3 are not modes: the enum jumps from Default (1) to Extreme (4).
        Assert.False(WmiWorkerValidation.TryParseOperatingMode(2, out _));
        Assert.False(WmiWorkerValidation.TryParseOperatingMode(3, out _));
        Assert.True(WmiWorkerValidation.TryParseOperatingMode((int)OperatingMode.Turbo, out var turbo));
        Assert.Equal(OperatingMode.Turbo, turbo);
    }

    [Fact]
    public void TryParseFanMode_RejectsUnknownValues()
    {
        Assert.False(WmiWorkerValidation.TryParseFanMode(7, out _));
        Assert.True(WmiWorkerValidation.TryParseFanMode((int)FanMode.Custom, out var custom));
        Assert.Equal(FanMode.Custom, custom);
    }

    // ---------------------------------------------------------------- framing and nonce

    [Fact]
    public async Task Framing_RoundTripsThroughAStream()
    {
        using var stream = new MemoryStream();
        await WmiWorkerProtocol.WriteAsync(stream, "{\"type\":\"hello\"}");
        stream.Position = 0;

        Assert.Equal("{\"type\":\"hello\"}", await WmiWorkerProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task Framing_RejectsAnOversizedLengthHeader()
    {
        using var stream = new MemoryStream();
        var header = BitConverter.GetBytes(WmiWorkerProtocol.MaxFrameBytes + 1);
        await stream.WriteAsync(header);
        stream.Position = 0;

        Assert.Null(await WmiWorkerProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task Framing_ReturnsNullWhenThePeerClosesMidFrame()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(64));
        await stream.WriteAsync(new byte[4]);
        stream.Position = 0;

        Assert.Null(await WmiWorkerProtocol.ReadAsync(stream));
    }

    [Fact]
    public void NonceMatches_IsCaseInsensitiveAndLengthStrict()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";

        Assert.True(WmiWorkerProtocol.NonceMatches(nonce, nonce.ToUpperInvariant()));
        Assert.False(WmiWorkerProtocol.NonceMatches(nonce, nonce[..^1]));
        Assert.False(WmiWorkerProtocol.NonceMatches(nonce, ""));
        Assert.False(WmiWorkerProtocol.NonceMatches("", nonce));
    }

    [Fact]
    public void GenerateNonce_IsSixtyFourHexCharacters()
    {
        var nonce = WmiWorkerClient.GenerateNonce();

        Assert.Equal(64, nonce.Length);
        Assert.True(StartupMode.IsHexNonce(nonce));
        Assert.NotEqual(nonce, WmiWorkerClient.GenerateNonce());
    }

    // ---------------------------------------------------------------- pipe end to end

    [Fact]
    public async Task Pipe_PingRoundTripsOverACurrentUserOnlyPipe()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync(WmiWorkerCommand.Ping);

        Assert.True(response.Ok);
        Assert.Equal(1, session.Executor.PingCalls);
    }

    [Fact]
    public async Task WorkerPipe_IsConnectableFromTheSameProcess()
    {
        // The worker is elevated and the window is not, so the pipe has to carry a Medium mandatory label: a plain
        // CurrentUserOnly pipe is created High and refuses the very process that spawned the worker. This pins that
        // the factory still produces a pipe both ends can use, whichever branch it took.
        var pipeName = NewPipeName();
        using var server = WorkerPipe.Create(pipeName);

        var accept = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accept.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(server.IsConnected);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Pipe_ServerFinishesCleanlyWhenTheClientDisconnects()
    {
        var session = await PipeSession.StartAsync();
        session.Client.Dispose();

        Assert.True(await session.ServerTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Pipe_RejectedHandshake_LeavesTheWorkerListening()
    {
        // A rogue client that connects first must not be able to take the helper away from its owner, and must not
        // be able to kill it either: the worker drops the connection and goes back to waiting.
        var pipeName = NewPipeName();
        var executor = new FakeExecutor();
        using var server = new WmiWorkerServer(pipeName, executor);

        var serverTask = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(15)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WmiWorkerClient.ConnectAsync(pipeName, "not-a-nonce", TimeSpan.FromSeconds(5)));

        using var client = await WmiWorkerClient.ConnectAsync(
            pipeName, WmiWorkerClient.GenerateNonce(), TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(WmiWorkerCommand.Ping);

        Assert.True(response.Ok);
        Assert.Equal(1, executor.PingCalls);

        // The server only finishes once the client goes away, so the handle is closed before the wait.
        client.Dispose();
        Assert.True(await serverTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Pipe_ClientThatIsTheParent_IsAccepted()
    {
        // The other half of the gate: the check has to let the real parent through, or the elevated path is dead.
        var pipeName = NewPipeName();
        var executor = new FakeExecutor();
        using var server = new WmiWorkerServer(pipeName, executor, expectedClientProcessId: Environment.ProcessId);

        var serverTask = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(10)));
        using var client = await WmiWorkerClient.ConnectAsync(
            pipeName, WmiWorkerClient.GenerateNonce(), TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(WmiWorkerCommand.Ping);

        Assert.True(response.Ok);
        Assert.Equal(1, executor.PingCalls);

        client.Dispose();
        Assert.True(await serverTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Pipe_ClientThatIsNotTheParent_IsRefusedAndTheWorkerKeepsListening()
    {
        // The process id comes from the kernel, so a caller cannot forge it. A pid that cannot be this test process
        // stands in for "some other program running as the same user".
        var pipeName = NewPipeName();
        var executor = new FakeExecutor();
        using var server = new WmiWorkerServer(pipeName, executor, expectedClientProcessId: 1);

        var serverTask = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(3)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WmiWorkerClient.ConnectAsync(pipeName, WmiWorkerClient.GenerateNonce(), TimeSpan.FromSeconds(2)));

        Assert.False(await serverTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, executor.PingCalls);
    }

    [Fact]
    public async Task Pipe_ClientRejectsAReplyThatDoesNotEchoTheNonce()
    {
        // The window proves it is talking to the helper it started by requiring its own value back. A server that
        // answers with a welcome but no echo is not that helper.
        var pipeName = NewPipeName();
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await WmiWorkerProtocol.ReadAsync(server);
            await WmiWorkerProtocol.WriteAsync(server, WmiWorkerProtocol.Serialize(new WmiWorkerResponse
            {
                Type = WmiWorkerProtocol.TypeWelcome,
                Ok = true
            }));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WmiWorkerClient.ConnectAsync(pipeName, WmiWorkerClient.GenerateNonce(), TimeSpan.FromSeconds(5)));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pipe_ClientRejectsAReplyThatIsNotAWelcome()
    {
        var pipeName = NewPipeName();
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await WmiWorkerProtocol.ReadAsync(server);
            await WmiWorkerProtocol.WriteAsync(server, WmiWorkerProtocol.Serialize(new WmiWorkerResponse
            {
                Type = WmiWorkerProtocol.TypeResult,
                Ok = true
            }));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WmiWorkerClient.ConnectAsync(pipeName, WmiWorkerClient.GenerateNonce(), TimeSpan.FromSeconds(5)));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pipe_UnknownCommand_IsRejectedWithoutReachingTheExecutor()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync((WmiWorkerCommand)99);

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, session.Executor.PingCalls);
    }

    [Fact]
    public async Task Pipe_FanPercentBelowTheFloor_IsRejectedBeforeWmi()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync(WmiWorkerCommand.SetFanSpeed, new JsonObject
        {
            ["cpuPercent"] = 15,
            ["gpuPercent"] = 60
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, session.Executor.SetFanSpeedCalls);
    }

    [Fact]
    public async Task Pipe_ValidFanPercent_ReachesTheExecutor()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync(WmiWorkerCommand.SetFanSpeed, new JsonObject
        {
            ["cpuPercent"] = 60,
            ["gpuPercent"] = 70
        });

        Assert.True(response.Ok);
        Assert.Equal(1, session.Executor.SetFanSpeedCalls);
        Assert.Equal((60, 70), session.Executor.LastFanSpeed);
    }

    [Fact]
    public async Task Pipe_MissingArgument_IsRejected()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync(WmiWorkerCommand.SetOperatingMode, new JsonObject());

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeInvalidArgument, response.ErrorCode);
        Assert.Equal(0, session.Executor.SetOperatingModeCalls);
    }

    [Fact]
    public async Task Pipe_NotSupportedFromTheExecutor_IsPassedThrough()
    {
        using var session = await PipeSession.StartAsync();
        session.Executor.BatterySupported = false;

        var response = await session.Client.SendAsync(WmiWorkerCommand.SetBatteryLimit, new JsonObject
        {
            ["mode"] = (int)BatteryChargeMode.Optimized80
        });

        Assert.False(response.Ok);
        Assert.Equal(WmiOperationResult.CodeNotSupported, response.ErrorCode);
    }

    [Fact]
    public async Task Pipe_TelemetryWithoutTheFlag_KeepsThePercentageFallback()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync(WmiWorkerCommand.ReadTelemetry, new JsonObject
        {
            ["cpuTempSensor"] = 1
        });

        Assert.True(response.Ok);
        Assert.False(session.Executor.LastSensorQuery!.TachometerConfirmed);
    }

    [Fact]
    public async Task Pipe_TelemetryWithAConfirmedTachometer_TellsTheExecutorToSkipTheFallback()
    {
        using var session = await PipeSession.StartAsync();

        var response = await session.Client.SendAsync(WmiWorkerCommand.ReadTelemetry, new JsonObject
        {
            ["tachometerConfirmed"] = true
        });

        Assert.True(response.Ok);
        Assert.True(session.Executor.LastSensorQuery!.TachometerConfirmed);
    }

    // ---------------------------------------------------------------- watchdog

    [Fact]
    public async Task ParentWatchdog_FiresWhenTheParentDisappears()
    {
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watchdog = ParentWatchdog.Start(
            parentProcessId: 4242,
            onParentGone: () => fired.TrySetResult(true),
            interval: TimeSpan.FromMilliseconds(20),
            isAlive: _ => false);

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ParentWatchdog_StaysQuietWhileTheParentLives()
    {
        var fired = false;

        using var watchdog = ParentWatchdog.Start(
            parentProcessId: 4242,
            onParentGone: () => fired = true,
            interval: TimeSpan.FromMilliseconds(20),
            isAlive: _ => true);

        await Task.Delay(200);
        Assert.False(fired);
    }

    [Fact]
    public void ParentWatchdog_IsProcessAlive_IsFalseForAnIdThatCannotExist()
    {
        Assert.False(ParentWatchdog.IsProcessAlive(0));
        Assert.False(ParentWatchdog.IsProcessAlive(-1));
        Assert.False(ParentWatchdog.IsProcessAlive(int.MaxValue));
    }

    // ---------------------------------------------------------------- host argument gate

    [Fact]
    public void Host_RejectsAMissingMode()
    {
        Assert.Equal(WmiWorkerHost.ExitInvalidArguments, WmiWorkerHost.Run(null!));
    }

    [Fact]
    public void Host_RejectsANormalStartupMode()
    {
        Assert.Equal(WmiWorkerHost.ExitInvalidArguments, WmiWorkerHost.Run(new StartupMode()));
    }

    [Fact]
    public void Host_RejectsAWorkerModeWithoutAPipe()
    {
        var mode = StartupMode.Parse(new[] { "--wmi-worker", "--parent-pid", "42" });

        Assert.Equal(WmiWorkerHost.ExitInvalidArguments, WmiWorkerHost.Run(mode));
    }

    // ---------------------------------------------------------------- helpers

    private static string NewPipeName() => "nitrolite-test-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A live server/client pair on a fresh pipe. Disposing it closes the client and waits for the server to notice,
    /// so a test cannot leave a background accept loop running into the next one.
    /// </summary>
    private sealed class PipeSession : IDisposable
    {
        private readonly WmiWorkerServer _server;

        private PipeSession(WmiWorkerServer server, WmiWorkerClient client, FakeExecutor executor, Task<bool> serverTask)
        {
            _server = server;
            Client = client;
            Executor = executor;
            ServerTask = serverTask;
        }

        public WmiWorkerClient Client { get; }
        public FakeExecutor Executor { get; }
        public Task<bool> ServerTask { get; }

        public static async Task<PipeSession> StartAsync()
        {
            var pipeName = NewPipeName();
            var nonce = WmiWorkerClient.GenerateNonce();
            var executor = new FakeExecutor();
            var server = new WmiWorkerServer(pipeName, executor);

            var serverTask = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(10)));
            var client = await WmiWorkerClient.ConnectAsync(pipeName, nonce, TimeSpan.FromSeconds(10));

            return new PipeSession(server, client, executor, serverTask);
        }

        public void Dispose()
        {
            Client.Dispose();
            try { ServerTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _server.Dispose();
        }
    }

    /// <summary>
    /// Stands in for the WMI executor so the pipe can be tested without hardware. It records what it was asked to do
    /// and answers with the same result shapes the real executor uses.
    /// </summary>
    private sealed class FakeExecutor : IWmiExecutor
    {
        public int PingCalls { get; private set; }
        public int SetFanSpeedCalls { get; private set; }
        public int SetFanBehaviorCalls { get; private set; }
        public int SetOperatingModeCalls { get; private set; }
        public int SetMuxModeCalls { get; private set; }
        public int SetBatteryLimitCalls { get; private set; }
        public (int Cpu, int Gpu) LastFanSpeed { get; private set; }
        public bool BatterySupported { get; set; } = true;

        public bool IsAvailable => true;

        public WmiOperationResult Ping()
        {
            PingCalls++;
            return WmiOperationResult.Success(new JsonObject { ["pong"] = true });
        }

        public WmiOperationResult GetCapabilities() => WmiOperationResult.Success(new JsonObject
        {
            ["gamingClass"] = true,
            ["batteryClass"] = BatterySupported
        });

        public SensorQuery? LastSensorQuery { get; private set; }

        public WmiOperationResult ReadTelemetry(SensorQuery query)
        {
            LastSensorQuery = query;
            return WmiOperationResult.Success(new JsonObject
            {
                ["cpuFanPercent"] = 34,
                ["gpuFanPercent"] = 34
            });
        }

        public WmiOperationResult ReadDeviceState() => WmiOperationResult.Success(new JsonObject
        {
            ["operatingMode"] = OperatingMode.Default.ToString()
        });

        public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu)
        {
            SetFanBehaviorCalls++;
            return WmiOperationResult.Success(new JsonObject
            {
                ["cpuFanMode"] = cpu.ToString(),
                ["gpuFanMode"] = gpu.ToString()
            });
        }

        public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent)
        {
            SetFanSpeedCalls++;
            LastFanSpeed = (cpuPercent, gpuPercent);
            return WmiOperationResult.Success(new JsonObject
            {
                ["cpuPercent"] = cpuPercent,
                ["gpuPercent"] = gpuPercent
            });
        }

        public WmiOperationResult SetOperatingMode(OperatingMode mode)
        {
            SetOperatingModeCalls++;
            return WmiOperationResult.Success(new JsonObject { ["operatingMode"] = mode.ToString() });
        }

        public WmiOperationResult SetMuxMode(GpuOperatingMode mode)
        {
            SetMuxModeCalls++;
            return WmiOperationResult.Success(new JsonObject { ["muxMode"] = mode.ToString() });
        }

        public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode)
        {
            SetBatteryLimitCalls++;
            return BatterySupported
                ? WmiOperationResult.Success(new JsonObject { ["batteryLimit"] = mode.ToString() })
                : WmiOperationResult.NotSupported("WMID_GUID5 is not registered on this machine");
        }

        public int GetKeyboardBacklightCalls { get; private set; }
        public int SetKeyboardLightingCalls { get; private set; }
        public KeyboardLightingRequest? LastKeyboardWrite { get; private set; }

        public WmiOperationResult GetKeyboardBacklight()
        {
            GetKeyboardBacklightCalls++;
            return WmiOperationResult.Success(new JsonObject
            {
                ["brightness"] = 100,
                ["red"] = 0,
                ["green"] = 255,
                ["blue"] = 0,
                ["rgbRaw"] = 0x0000000000FF0000UL
            });
        }

        public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request)
        {
            SetKeyboardLightingCalls++;
            LastKeyboardWrite = request;
            return WmiOperationResult.Success(new JsonObject { ["effect"] = request.Effect });
        }

        public WmiOperationResult GetCoolBoost() => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult SetCoolBoost(bool on) => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult SetWinKeyLock(bool enabled) => WmiOperationResult.Success();

        public WmiOperationResult GetBacklightTimeout() => WmiOperationResult.NotSupported("APGeAction is not registered");

        public WmiOperationResult SetBacklightTimeout(bool enabled) => WmiOperationResult.NotSupported("APGeAction is not registered");

        public void Dispose() { }
    }
}

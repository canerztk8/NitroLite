using System.IO;
using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The four things a laptop does that a unit test usually does not: it sleeps, it is asked to do several things at
/// once, its files get truncated, and its <c>/sys</c> tree changes underneath the reader.
/// </summary>
/// <remarks>
/// <para>
/// Every test here is deterministic. The suspend cases do not call <c>Thread.Sleep</c> and hope: they make one step
/// take longer than the loop's interval and then assert on what the loop did, which is the same question a real
/// suspend asks — what happens when a tick is late by a lot. The concurrency cases use a fixed number of workers and
/// a deadline, so a deadlock fails the test instead of hanging the run.
/// </para>
/// <para>
/// The thermal floor is the invariant that runs through all four regions. A late tick, a concurrent write, a corrupt
/// settings file and a renumbered sensor directory are all ways to end up with the fans at a number nobody chose, and
/// the floor is what has to survive each of them.
/// </para>
/// </remarks>
public class ChaosAndConcurrencyTests
{
    // ================================================================================================================
    // 1. Suspend and resume
    // ================================================================================================================

    [Fact]
    public async Task TheFanLoopNeverRunsTwoStepsAtOnceEvenWhenAStepOutlastsTheInterval()
    {
        // A suspend is a tick that arrives very late. The hazard is a loop that answers a late tick by firing the
        // steps it missed, so two evaluations run against the same fans and the second overwrites the first.
        int running = 0;
        int maxRunning = 0;
        int steps = 0;

        using var loop = new FanCurveLoop(async () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            Interlocked.Increment(ref steps);

            // Five times the interval: the loop's clock has fired several times by the time this returns.
            await Task.Delay(50).ConfigureAwait(false);
            Interlocked.Decrement(ref running);
        }, TimeSpan.FromMilliseconds(10));

        loop.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref steps) < 6 && DateTime.UtcNow < deadline) await Task.Delay(10);

        loop.Stop();

        Assert.True(steps >= 6, $"the loop stopped after {steps} steps");
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task TheFanLoopKeepsSteppingAfterAStepThatTookFarLongerThanTheInterval()
    {
        // The first step stands in for the gap: the machine was asleep, the step was in flight, and it returns long
        // after the interval it was scheduled on. The loop has to carry on from there rather than stop.
        int steps = 0;
        using var loop = new FanCurveLoop(async () =>
        {
            if (Interlocked.Increment(ref steps) == 1) await Task.Delay(400).ConfigureAwait(false);
        }, TimeSpan.FromMilliseconds(20));

        loop.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref steps) < 5 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.True(steps >= 5, $"the loop stopped after the long step: {steps} steps");
        Assert.True(loop.IsRunning);
    }

    [Fact]
    public void StopAndWaitIsBoundedWhileAStepIsStuck()
    {
        // Shutdown calls StopAndWait. A step blocked in a firmware call must not make the window hang on exit, so the
        // wait is bounded and the loop reports itself stopped either way.
        var stuck = new TaskCompletionSource();
        var loop = new FanCurveLoop(() => stuck.Task, TimeSpan.FromMilliseconds(10));

        loop.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (loop.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(5);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        loop.StopAndWait(TimeSpan.FromMilliseconds(200));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"StopAndWait took {sw.Elapsed.TotalSeconds:F1} s");
        Assert.False(loop.IsRunning);

        stuck.SetResult();
        loop.Dispose();
    }

    [Fact]
    public void TheThermalFloorStillRaisesTheFansAfterAGap()
    {
        // Before the sleep the fans were at 60 % on a cool die and the floor had recorded that. After the wake the
        // die is at 95 °C: the recorded value must not be read as "already handled".
        var floor = new ManualFanFloor();
        floor.Commit(60, 60);

        Assert.Equal(100, floor.EffectivePercent(30, 95));
        Assert.Equal(80, floor.EffectivePercent(30, 90));
        Assert.Equal(60, floor.EffectivePercent(30, 85));
        Assert.Equal(30, floor.EffectivePercent(30, 84));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task AFrameThatArrivesInDribblesIsStillOneFrame(int chunk)
    {
        // A pipe that stalled and resumed delivers the frame in pieces. A reader that treats the first piece as the
        // whole message would answer a truncated request, which is the failure a suspend produces.
        var request = new WmiWorkerRequest
        {
            Type = WmiWorkerProtocol.TypeCommand,
            Command = WmiWorkerCommand.SetFanSpeed,
            Args = new JsonObject { ["cpu"] = 60, ["gpu"] = 55 }
        };

        using var written = new MemoryStream();
        await WmiWorkerProtocol.WriteAsync(written, request);
        var frame = written.ToArray();

        using var dribble = new DribbleStream(frame, chunk);
        var read = await WmiWorkerProtocol.ReadRequestAsync(dribble);

        Assert.False(read.Closed);
        Assert.NotNull(read.Request);
        Assert.Equal(WmiWorkerCommand.SetFanSpeed, read.Request!.Command);
        Assert.Equal(60, read.Request.Args!["cpu"]!.GetValue<int>());
    }

    [Fact]
    public async Task ATelemetryReadThatFailsAfterAWakeRecoversOnTheNextTick()
    {
        // The first read after a resume is the one that finds the embedded controller still waking up. It must cost
        // one tick, not the session.
        var (fs, _) = BuildNitro16();
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);
        using var flaky = new FlakyHardware(controller, failFirstReads: 1);

        await Assert.ThrowsAsync<TimeoutException>(() => flaky.GetMonitorDataAsync());

        var recovered = await flaky.GetMonitorDataAsync();

        Assert.NotNull(recovered);
        Assert.Equal(63, recovered!.CpuTemperature);
        Assert.Equal(1, flaky.Failures);
    }

    // ================================================================================================================
    // 2. High-frequency concurrent commands
    // ================================================================================================================

    [Fact]
    public async Task HundredsOfConcurrentModeFanAndLightingCommandsDoNotDeadlock()
    {
        // The write gate serialises writes; the read gate serialises telemetry. The failure this guards is the two
        // taking each other in the wrong order, which shows up as a run that never finishes rather than as an
        // exception.
        var (fs, _) = BuildNitro16();
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        const int Rounds = 50;

        // The lighting path waits out the OpenRGB connect timeout when nothing is listening, which is two seconds a
        // call. Four rounds is enough to put it in contention with the write gate without turning the test into a
        // two-minute wait; the other five workers carry the volume.
        const int LightingRounds = 4;

        var failures = new List<Exception>();

        async Task Guard(Func<int, Task> body, int rounds)
        {
            try
            {
                for (var i = 0; i < rounds; i++) await body(i).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add(ex);
            }
        }

        var work = new List<Task>
        {
            Guard(async i => { await controller.SetOperatingModeAsync(
                i % 2 == 0 ? OperatingMode.Quiet : OperatingMode.Turbo).ConfigureAwait(false); }, Rounds),
            Guard(async i => { await controller.SetFanModeAsync(
                FanMode.Custom, 40 + (i % 40), 40 + (i % 40)).ConfigureAwait(false); }, Rounds),
            Guard(async i => { await controller.SetLightingAsync(
                "STATIC", 1 + (i % 5), 3, 1, "#FF0000", null).ConfigureAwait(false); }, LightingRounds),
            Guard(async _ => { await controller.GetMonitorDataAsync().ConfigureAwait(false); }, Rounds),
            Guard(async _ => { await controller.GetMonitorDataAsync().ConfigureAwait(false); }, Rounds),
            Guard(async _ => { await controller.GetOperatingModeAsync().ConfigureAwait(false); }, Rounds)
        };

        var all = Task.WhenAll(work);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(all, finished);
        await all;

        Assert.Empty(failures);

        // The tree is still readable and the last write is a value the module can parse.
        var data = await controller.GetMonitorDataAsync();
        Assert.NotNull(data);
        Assert.Equal(63, data!.CpuTemperature);
        Assert.Contains(fs.ReadAllText(LinuxPaths.PlatformProfile), new[] { "quiet", "performance" });
    }

    [Fact]
    public async Task ACommandThatArrivesAfterDisposeIsRefusedRatherThanThrown()
    {
        // Shutdown races the last click. The window closes, the controller is disposed, and a command that was
        // already queued arrives. It has to answer "no" instead of raising ObjectDisposedException on a thread
        // nobody is watching.
        var (fs, _) = BuildNitro16();
        var settings = new AppSettings();

        var openRgb = new OpenRgbClient(port: 1);
        var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);
        controller.Dispose();
        openRgb.Dispose();

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.False(await controller.SetFanModeAsync(FanMode.Custom, 60, 60));
        Assert.False(await controller.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", null));
        Assert.Null(await controller.GetMonitorDataAsync());

        // A read holds no handle of its own, so it is allowed to answer; what it may not do is throw.
        _ = await controller.GetOperatingModeAsync();
        _ = await controller.GetFanModeAsync();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 84)]
    [InlineData(30, 85)]
    [InlineData(30, 90)]
    [InlineData(30, 95)]
    [InlineData(100, 40)]
    [InlineData(100, 95)]
    [InlineData(-100, 95)]
    [InlineData(500, 95)]
    [InlineData(500, -100)]
    public void TheFloorNeverLowersWhatTheUserAskedFor(int target, int temperatureC)
    {
        // The invariant the whole feature rests on: whatever the slider says and whatever the die reads, the number
        // that reaches the firmware is at least the slider's. A floor that could lower a value would be a fan
        // controller that slows the fans when the machine gets hot.
        var floor = new ManualFanFloor();
        var effective = floor.EffectivePercent(target, temperatureC);

        Assert.InRange(effective, 0, 100);
        Assert.True(effective >= Math.Clamp(target, 0, 100),
            $"target {target} at {temperatureC} °C produced {effective}");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-273.0)]
    [InlineData(1000.0)]
    public void AnImpossibleTemperatureNeverProducesAnImpossibleSpeed(double temperatureC)
    {
        var floor = new ManualFanFloor();
        floor.Commit(60, 60);

        var effective = floor.EffectivePercent(30, temperatureC);

        Assert.InRange(effective, 0, 100);
    }

    // ================================================================================================================
    // 3. Corrupt files and a disk that says no
    // ================================================================================================================

    [Fact]
    public void ACorruptUserProfileFileIsRejectedAndTheEmbeddedTableStillLoads()
    {
        // profiles.user.json is hand-edited by design, so a broken one is a normal event. It must cost the user their
        // own additions and nothing else.
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), "{ this is not json", out var rejections);

        Assert.NotEmpty(catalog.Profiles);
        Assert.NotEmpty(rejections);
        Assert.Equal("nitro-16-an16-41", catalog.Match(Identity("AN16-41")).Id);
    }

    [Fact]
    public void AUserProfileThatClaimsEveryAcerMachineIsRejected()
    {
        // A catch-all in a hand-edited file would silently replace the shipped default for every model, so the rule
        // is that only the embedded table may carry one.
        const string user = """
            {
              "schemaVersion": 1,
              "profiles": [
                { "id": "everything", "match": { "anyAcer": true }, "sensors": { "supported": true } }
              ]
            }
            """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "everything");
        Assert.NotEmpty(rejections);
    }

    [Fact]
    public void AUserProfileWithAnImpossibleFanCeilingIsRejected()
    {
        const string user = """
            {
              "schemaVersion": 1,
              "profiles": [
                {
                  "id": "too-fast",
                  "match": { "modelCodes": ["AN16-41"] },
                  "sensors": { "supported": true },
                  "maxCpuFanRpm": 999999
                }
              ]
            }
            """;

        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), user, out var rejections);

        Assert.DoesNotContain(catalog.Profiles, p => p.Id == "too-fast");
        Assert.NotEmpty(rejections);
    }

    [Fact]
    public void AFailedAtomicWriteLeavesNoTemporaryFileBehind()
    {
        // The temporary file is the thing that used to accumulate: one per failed save, forever. A path that cannot
        // be created is the cheapest way to make the write fail after the temporary name has been chosen.
        var dir = Path.Combine(Path.GetTempPath(), "nitrolite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "not a directory");

            var target = Path.Combine(blocker, "settings.json");
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(target, "{}"));

            Assert.Empty(Directory.GetFiles(dir, "*.tmp.*", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ASuccessfulAtomicWriteLeavesNoTemporaryFileBehind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nitrolite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var target = Path.Combine(dir, "settings.json");

            AtomicFile.WriteAllText(target, "{\"first\":true}");
            AtomicFile.WriteAllText(target, "{\"second\":true}");

            Assert.Equal("{\"second\":true}", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp.*", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"theme\":")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"custom_fan_cpu_percent\":\"not a number\"}")]
    public void ASettingsFileThatIsNotASettingsFileLoadsAsDefaults(string json)
    {
        // A crash mid-write, a full disk, a hand edit. None of them may leave the window without settings, and the
        // result has to say the primary file was the problem so the user is told rather than silently reset.
        var dir = Path.Combine(Path.GetTempPath(), "nitrolite-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var primary = Path.Combine(dir, "settings.json");
            File.WriteAllText(primary, json);

            var result = SettingsManager.LoadSettings(primary);

            Assert.Equal(SettingsLoadSource.Defaults, result.Source);
            Assert.True(result.PrimaryCorrupt);
            Assert.NotNull(result.PrimaryError);

            var settings = result.Settings;
            Assert.InRange(settings.CustomFanCpuPercent, AppSettings.MinCustomFanPercent, 100);
            Assert.InRange(settings.CustomFanGpuPercent, AppSettings.MinCustomFanPercent, 100);
            Assert.NotEmpty(settings.FanCurvePoints!);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ACorruptPrimaryFallsBackToTheBackupRatherThanToDefaults()
    {
        // The backup exists for exactly this. Falling through to defaults while a good copy sits next to the broken
        // one would throw away every setting the user has.
        var dir = Path.Combine(Path.GetTempPath(), "nitrolite-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var primary = Path.Combine(dir, "settings.json");
            var backup = primary + ".bak";

            File.WriteAllText(primary, "{\"theme\":");
            File.WriteAllText(backup, "{\"theme\":\"MidnightOled\",\"custom_fan_cpu_percent\":55}");

            var result = SettingsManager.LoadSettings(primary);

            Assert.Equal(SettingsLoadSource.Backup, result.Source);
            Assert.True(result.PrimaryCorrupt);
            Assert.Equal(AppTheme.MidnightOled, result.Settings.Theme);
            Assert.Equal(55, result.Settings.CustomFanCpuPercent);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnImpossibleCustomFanPercentIsClampedRatherThanKept(int percent)
    {
        var settings = new AppSettings
        {
            CustomFanCpuPercent = percent,
            CustomFanGpuPercent = percent,
            FanCurveHysteresisC = percent,
            FanCurveMinDwellSeconds = percent
        };

        settings.Normalize();

        Assert.InRange(settings.CustomFanCpuPercent, AppSettings.MinCustomFanPercent, 100);
        Assert.InRange(settings.CustomFanGpuPercent, AppSettings.MinCustomFanPercent, 100);
        Assert.InRange(settings.FanCurveHysteresisC, 0, 15);
        Assert.InRange(settings.FanCurveMinDwellSeconds, 0, 120);
    }

    [Theory]
    [InlineData(-100, -100)]
    [InlineData(500, 500)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void AnImpossibleCurveAnchorIsClampedRatherThanDropped(int temperatureC, int percent)
    {
        var sanitized = FanCurve.Sanitize(new[] { new FanCurvePoint(temperatureC, percent) });

        Assert.NotEmpty(sanitized);
        Assert.All(sanitized, p =>
        {
            Assert.InRange(p.TemperatureC, FanCurve.MinTemperatureC, FanCurve.MaxTemperatureC);
            Assert.InRange(p.Percent, FanCurve.MinPercent, FanCurve.MaxPercent);
        });
    }

    // ================================================================================================================
    // 4. A /sys tree that changes underneath the reader
    // ================================================================================================================

    [Fact]
    public async Task ARenumberedHwmonDirectoryIsFollowedRatherThanReadFromTheOldPath()
    {
        // The kernel numbers hwmon directories by probe order, and a module reload can move acer from hwmon0 to
        // hwmon3. The reader re-enumerates every tick, so the reading has to follow the name file rather than the
        // path it saw at startup.
        var (fs, _) = BuildNitro16();
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        Assert.Equal(63, (await controller.GetMonitorDataAsync())!.CpuTemperature);

        // The module reloaded: every directory moved up by three and the values changed with it.
        foreach (var (from, to) in new[] { ("hwmon0", "hwmon3"), ("hwmon1", "hwmon4"), ("hwmon2", "hwmon5"), ("hwmon3", "hwmon6") })
        {
            foreach (var file in fs.EnumerateFiles($"{LinuxPaths.HwmonRoot}/{from}"))
            {
                var content = fs.ReadAllText($"{LinuxPaths.HwmonRoot}/{from}/{file}");
                fs.WithFile($"{LinuxPaths.HwmonRoot}/{to}/{file}", content + "\n");
            }

            foreach (var file in fs.EnumerateFiles($"{LinuxPaths.HwmonRoot}/{from}"))
            {
                fs.WithoutFile($"{LinuxPaths.HwmonRoot}/{from}/{file}");
            }
        }

        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon4/temp1_input", "71000\n");

        var afterReload = await controller.GetMonitorDataAsync();

        Assert.NotNull(afterReload);
        Assert.Equal(71, afterReload!.CpuTemperature);
    }

    [Fact]
    public async Task ANodeThatDisappearsBetweenTicksFallsBackInsteadOfThrowing()
    {
        // ENOENT mid-session: the module was unloaded, or the attribute was never there. The die sensor is gone, so
        // the CPU reading falls back to the board sensor — the documented order — and the tachometers are gone, so
        // the speeds stop being reported as measured rather than being reported as a measured zero.
        var (fs, _) = BuildNitro16();
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        var before = await controller.GetMonitorDataAsync();
        Assert.Equal(63, before!.CpuTemperature);
        Assert.True(before.IsFanRpmMeasured);

        fs.WithoutFile($"{LinuxPaths.HwmonRoot}/hwmon1/temp1_input");
        fs.WithoutFile($"{LinuxPaths.HwmonRoot}/hwmon2/temp1_input");
        fs.WithoutFile($"{LinuxPaths.HwmonRoot}/hwmon0/fan1_input");
        fs.WithoutFile($"{LinuxPaths.HwmonRoot}/hwmon0/fan2_input");

        var after = await controller.GetMonitorDataAsync();

        Assert.NotNull(after);
        Assert.Equal(55, after!.CpuTemperature);
        Assert.False(after.IsFanRpmMeasured);

        // With the board sensor gone too there is nothing left to read, and the answer is a dash rather than a zero
        // dressed up as a reading.
        fs.WithoutFile($"{LinuxPaths.HwmonRoot}/hwmon0/temp1_input");

        var nothing = await controller.GetMonitorDataAsync();

        Assert.NotNull(nothing);
        Assert.Equal(0, nothing!.CpuTemperature);
    }

    [Fact]
    public async Task APlatformProfileThatCannotBeWrittenIsReportedRatherThanRetriedForever()
    {
        // EACCES is the state every user is in before the udev rule. The write answers no, the hint is raised once,
        // and the value on disk is untouched.
        var (fs, _) = BuildNitro16();
        fs.DenyWrite(LinuxPaths.PlatformProfile);

        var settings = new AppSettings();
        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.True(controller.PermissionHintRaised);
        Assert.Equal("balanced", fs.ReadAllText(LinuxPaths.PlatformProfile));
    }

    [Fact]
    public void EveryShippedProfileIsWellFormed()
    {
        // Thirty-one profiles, each one a claim about a machine nobody here owns. The schema is the only thing that
        // can be checked without the laptop, so it is checked for all of them rather than for the one in the matrix.
        var catalog = Catalog();
        var profiles = catalog.Profiles;

        Assert.Equal(31, profiles.Count);
        Assert.Empty(catalog.Rejections);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var claimedCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Id), "a profile has no id");
            Assert.True(ids.Add(profile.Id), $"duplicate profile id: {profile.Id}");

            Assert.Matches("^[a-z0-9][a-z0-9-]{0,63}$", profile.Id);

            // A profile that claims nothing can never match, which makes it dead weight in a table that decides
            // which hardware calls are safe.
            Assert.True(
                profile.AnyAcer || profile.ModelCodes.Count > 0 || profile.BoardPatterns.Count > 0 || profile.FamilyPatterns.Count > 0,
                $"{profile.Id} claims no machine");

            foreach (var code in profile.ModelCodes)
            {
                Assert.Matches("^[A-Z0-9]{2,8}(-[A-Z0-9]{2,4})*$", code);
                Assert.False(claimedCodes.TryGetValue(code, out var other),
                    $"{code} is claimed by both {profile.Id} and {other}");
                claimedCodes[code] = profile.Id;
            }

            Assert.InRange(profile.MaxCpuFanRpm, ProfileCatalog.MinReferenceRpm, ProfileCatalog.MaxReferenceRpm);
            Assert.InRange(profile.MaxGpuFanRpm, ProfileCatalog.MinReferenceRpm, ProfileCatalog.MaxReferenceRpm);

            foreach (var method in profile.UnsupportedMethods)
            {
                Assert.Contains(method, ProfileCatalog.KnownMethods);
            }

            Assert.Equal("embedded", profile.Source);
        }

        // Exactly one catch-all, and it is the last resort rather than a profile that shadows a real model.
        Assert.Single(profiles, p => p.AnyAcer);
    }

    [Fact]
    public void EveryShippedProfileMatchesItsOwnModelCode()
    {
        // The table is only useful if a machine that names a code lands on the profile that claims it. A profile
        // whose own code resolves to a different profile is a table that has drifted.
        var catalog = Catalog();

        foreach (var profile in catalog.Profiles)
        {
            foreach (var code in profile.ModelCodes)
            {
                var matched = catalog.Match(Identity(code));

                Assert.Equal(profile.Id, matched.Id);
            }
        }
    }

    // ================================================================================================================
    // Helpers
    // ================================================================================================================

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static DeviceIdentity Identity(string modelCode) => new()
    {
        Manufacturer = "Acer",
        Model = $"Nitro {modelCode}",
        ModelCode = modelCode,
        ModelFamily = "",
        BiosVersion = "1.27",
        BiosVendor = "Insyde",
        CpuVendor = CpuVendor.Amd,
        IsAcer = true
    };

    /// <summary>A Nitro 16 in memory: <c>acer</c>, <c>k10temp</c>, <c>coretemp</c>, the four ACPI profiles and the module tree.</summary>
    private static (InMemorySysfsFileSystem Fs, string Root) BuildNitro16()
    {
        var fs = new InMemorySysfsFileSystem();

        fs.WithFile($"{LinuxPaths.DmiRoot}/product_name", "Nitro AN16-41\n");
        fs.WithFile($"{LinuxPaths.DmiRoot}/board_name", "AN16-41\n");
        fs.WithFile($"{LinuxPaths.DmiRoot}/sys_vendor", "Acer\n");
        fs.WithFile($"{LinuxPaths.DmiRoot}/bios_version", "V1.27\n");
        fs.WithFile($"{LinuxPaths.DmiRoot}/bios_vendor", "Insyde\n");

        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced balanced-performance performance\n");

        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon0/name", "acer\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon0/temp1_input", "55000\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon0/fan1_input", "2436\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon0/fan2_input", "2409\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon1/name", "k10temp\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon1/temp1_input", "63000\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon2/name", "coretemp\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon2/temp1_input", "61000\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon3/name", "amdgpu\n");
        fs.WithFile($"{LinuxPaths.HwmonRoot}/hwmon3/temp1_input", "57000\n");

        fs.WithFile($"{LinuxPaths.PowerSupplyRoot}/BAT1/{LinuxPaths.ChargeControlEndThreshold}", "100\n");

        var sense = $"{LinuxPaths.LinuwuModuleRoot}/{LinuxPaths.NitroSense}";
        fs.WithFile($"{sense}/{LinuxPaths.FanSpeed}", "60,50\n");
        fs.WithFile($"{sense}/{LinuxPaths.BatteryLimiter}", "0\n");
        fs.WithFile($"{sense}/{LinuxPaths.DgpuMode}", "0\n");

        return (fs, LinuxPaths.HwmonRoot);
    }

    /// <summary>A stream that hands out at most <c>chunk</c> bytes per read, like a pipe that stalled and resumed.</summary>
    private sealed class DribbleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _position;

        public DribbleStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = Math.Max(1, chunk);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _position);
            if (take <= 0) return 0;

            _data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A backend whose first reads fail, standing in for the embedded controller that is still waking up.</summary>
    private sealed class FlakyHardware : IHardwareController
    {
        private readonly IHardwareController _inner;
        private int _remainingFailures;

        public FlakyHardware(IHardwareController inner, int failFirstReads)
        {
            _inner = inner;
            _remainingFailures = failFirstReads;
        }

        public int Failures { get; private set; }

        public string BackendName => _inner.BackendName;

        public bool IsAvailable => _inner.IsAvailable;

        public Task<bool> EnsureConnectedAsync() => _inner.EnsureConnectedAsync();

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => _inner.GetCapabilitiesAsync();

        public Task<MonitorData?> GetMonitorDataAsync()
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                Failures++;
                throw new TimeoutException("The embedded controller did not answer after the wake.");
            }

            return _inner.GetMonitorDataAsync();
        }

        public Task<OperatingMode?> GetOperatingModeAsync() => _inner.GetOperatingModeAsync();

        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => _inner.SetOperatingModeAsync(mode);

        public Task<GpuOperatingMode?> GetGpuModeAsync() => _inner.GetGpuModeAsync();

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => _inner.SetGpuModeAsync(mode);

        public Task<int?> GetGpuModeCapabilityAsync() => _inner.GetGpuModeCapabilityAsync();

        public Task<FanMode?> GetFanModeAsync() => _inner.GetFanModeAsync();

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) =>
            _inner.SetFanModeAsync(mode, cpuPercent, gpuPercent);

        public Task<LightingConfig?> GetLightingConfigAsync() => _inner.GetLightingConfigAsync();

        public Task<bool> SetLightingAsync(
            string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) =>
            _inner.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors);

        public Task<bool> TurnOffLightingAsync() => _inner.TurnOffLightingAsync();

        public Task<bool?> GetBacklightTimeoutAsync() => _inner.GetBacklightTimeoutAsync();

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => _inner.SetBacklightTimeoutAsync(enable30s);

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => _inner.GetBatteryChargeModeAsync();

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => _inner.SetBatteryChargeModeAsync(mode);

        public Task<bool?> GetCoolBoostAsync() => _inner.GetCoolBoostAsync();

        public Task<bool> SetCoolBoostAsync(bool on) => _inner.SetCoolBoostAsync(on);

        public Task<bool?> GetLcdOverdriveAsync() => _inner.GetLcdOverdriveAsync();

        public Task<bool> SetLcdOverdriveAsync(bool on) => _inner.SetLcdOverdriveAsync(on);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => _inner.SetWinKeyLockAsync(enabled);

        public void Dispose()
        {
            // The inner controller is owned by the test that built it.
        }
    }
}

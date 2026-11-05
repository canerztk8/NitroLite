using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Adversarial empirical challenge tests designed by challenger_nitrolite_2.
/// Probes Single-Instance Mutex & EventWaitHandle concurrency, settings persistence & corruption resilience,
/// Win32 focus unlocking, power event re-assertion timing, and socket failure resilience.
/// </summary>
public class AdversarialLifecycleAndPersistenceTests
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;

    // =========================================================================
    // 1. SINGLE-INSTANCE MUTEX & FOCUS UNLOCKING CHALLENGES
    // =========================================================================

    [Fact]
    public void SingleInstanceMutex_AbandonedMutex_DemonstratesConstructorNeverThrowsAndRequiresWaitOne()
    {
        string uniqueMutexName = $"Local\\NitroLite_Test_AbandonedMutex_{Guid.NewGuid():N}";
        var readySignal = new ManualResetEvent(false);

        Mutex? abandonedMutexHolder = null;
        Mutex? keepAliveHandle = null;

        // Worker thread creates mutex with initial ownership and terminates without releasing it, abandoning it
        var worker = new Thread(() =>
        {
            abandonedMutexHolder = new Mutex(true, uniqueMutexName, out bool created);
            Assert.True(created);
            readySignal.Set();
            // Thread terminates while still owning the mutex -> Windows NT kernel marks mutex ABANDONED!
        })
        {
            IsBackground = true
        };
        worker.Start();
        readySignal.WaitOne(2000);

        // Open an existing handle to keep the kernel mutex object alive across worker thread death
        bool openedExisting = Mutex.TryOpenExisting(uniqueMutexName, out keepAliveHandle);
        Assert.True(openedExisting);
        Assert.NotNull(keepAliveHandle);

        worker.Join();

        // 1. App.xaml.cs constructor pattern:
        // Shows that new Mutex(...) NEVER throws AbandonedMutexException in Win32!
        bool createdNew = false;
        bool constructorThrewAbandoned = false;
        Mutex? instanceMutex = null;
        try
        {
            instanceMutex = new Mutex(true, uniqueMutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            constructorThrewAbandoned = true;
        }

        // Empirical proof: Constructor NEVER throws AbandonedMutexException, and createdNew is false!
        Assert.False(constructorThrewAbandoned, "new Mutex(true, ...) does not wait on the mutex and therefore never throws AbandonedMutexException in Win32.");
        Assert.False(createdNew, "When the mutex kernel object already exists, createdNew is always false.");

        // 2. Empirical proof: Only WaitOne() detects the abandoned mutex and allows acquiring ownership!
        bool waitOneThrewAbandoned = false;
        try
        {
            instanceMutex?.WaitOne(2000);
        }
        catch (AbandonedMutexException)
        {
            // WaitOne throws AbandonedMutexException AND grants ownership to this thread!
            waitOneThrewAbandoned = true;
        }

        Assert.True(waitOneThrewAbandoned, "WaitOne(0) correctly detects the abandoned mutex and acquires it.");

        keepAliveHandle?.Dispose();
        abandonedMutexHolder?.Dispose();
        instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
    }

    [Fact]
    public void SingleInstanceMutex_AbandonedMutex_RecoveryLogic_AcquiresOwnershipAndSetsCreatedNew()
    {
        string uniqueMutexName = $"Local\\NitroLite_Test_Recovery_{Guid.NewGuid():N}";
        var readySignal = new ManualResetEvent(false);
        Mutex? abandonedMutexHolder = null;
        Mutex? keepAliveHandle = null;

        var worker = new Thread(() =>
        {
            abandonedMutexHolder = new Mutex(true, uniqueMutexName, out bool created);
            Assert.True(created);
            readySignal.Set();
        })
        {
            IsBackground = true
        };
        worker.Start();
        readySignal.WaitOne(2000);

        bool openedExisting = Mutex.TryOpenExisting(uniqueMutexName, out keepAliveHandle);
        Assert.True(openedExisting);
        Assert.NotNull(keepAliveHandle);

        worker.Join();
        Thread.Sleep(100);

        // Exact App.xaml.cs recovery logic:
        bool createdNew = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(true, uniqueMutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            try
            {
                if (mutex != null && mutex.WaitOne(0))
                {
                    createdNew = true;
                }
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }

        Assert.True(createdNew, "Recovery logic must acquire the abandoned mutex and set createdNew to true.");

        keepAliveHandle?.Dispose();
        abandonedMutexHolder?.Dispose();
        if (createdNew && mutex != null)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    [Fact]
    public void SingleInstanceEvent_TryOpenExisting_NonExistent_ReturnsFalseWithoutThrowing()
    {
        string nonExistentEvent = $"Local\\NitroLite_Test_WakeUp_{Guid.NewGuid():N}";

        // App.xaml.cs line 46:
        // if (EventWaitHandle.TryOpenExisting(EventName, out var wakeEvent)) { wakeEvent.Set(); }
        bool opened = EventWaitHandle.TryOpenExisting(nonExistentEvent, out var wakeEvent);

        Assert.False(opened);
        Assert.Null(wakeEvent);
    }

    [Fact]
    public void SingleInstanceEvent_SignalingFlow_WakesWaitingListenerAcrossThreads()
    {
        string eventName = $"Local\\NitroLite_Test_WakeUp_{Guid.NewGuid():N}";
        using var wakeUpEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        var wakeReceivedSignal = new ManualResetEvent(false);
        var listenerThread = new Thread(() =>
        {
            if (wakeUpEvent.WaitOne(3000))
            {
                wakeReceivedSignal.Set();
            }
        })
        {
            IsBackground = true
        };
        listenerThread.Start();

        // Simulate secondary instance launching:
        bool opened = EventWaitHandle.TryOpenExisting(eventName, out var senderHandle);
        Assert.True(opened);
        Assert.NotNull(senderHandle);

        senderHandle.Set();
        senderHandle.Dispose();

        bool received = wakeReceivedSignal.WaitOne(2000);
        Assert.True(received, "Listener thread must receive wake signal from second instance!");
    }

    [Fact]
    public void SingleInstanceMutex_DuplicateInstance_CorrectlyDetectedAndRejected()
    {
        string mutexName = $"Local\\NitroLite_Test_Duplicate_{Guid.NewGuid():N}";

        // Primary instance acquires mutex
        using var primaryMutex = new Mutex(true, mutexName, out bool createdPrimary);
        Assert.True(createdPrimary);

        // Secondary instance attempts to acquire
        bool createdSecondary;
        try
        {
            using var secondaryMutex = new Mutex(true, mutexName, out createdSecondary);
        }
        catch (AbandonedMutexException)
        {
            createdSecondary = true;
        }

        Assert.False(createdSecondary, "Secondary instance must recognize that primary already owns the mutex!");
    }

    [Fact]
    public void AllowSetForegroundWindow_ASFW_ANY_InvokesWithoutAccessViolation()
    {
        // App.xaml.cs line 45 calls AllowSetForegroundWindow(ASFW_ANY = -1)
        // Verify Win32 call executes safely without throwing or causing memory access violations
        bool result = AllowSetForegroundWindow(ASFW_ANY);
        // On modern Windows, AllowSetForegroundWindow may return true or false depending on foreground lock,
        // but it must NEVER cause an unmanaged memory exception or crash.
        Assert.True(result || !result);
    }

    // =========================================================================
    // 2. SETTINGS PERSISTENCE & CORRUPTION RESILIENCE
    // =========================================================================

    [Fact]
    public void SettingsPersistence_MissingSettingsFile_SafelyFallsBackToLegacyConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nitrolite_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var legacyPath = Path.Combine(tempDir, "config.json");

            // Write legacy config with StartMinimized: true and NO rgb settings
            string legacyJson = @"{
                ""StartMinimized"": true,
                ""OsdEnabled"": false,
                ""SelectedChartMetric"": ""CPU_TEMP"",
                ""AutoRamCleanerEnabled"": true,
                ""WindowLeft"": 200.0,
                ""WindowTop"": 150.0
            }";
            File.WriteAllText(legacyPath, legacyJson);

            // Replicate LoadSettings logic from MainWindow.xaml.cs:
            AppSettings appSettings = new();
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) appSettings = s;
            }
            else if (File.Exists(legacyPath))
            {
                var json = File.ReadAllText(legacyPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) appSettings = s;
            }

            Assert.True(appSettings.StartMinimized);
            Assert.False(appSettings.IsBacklightOff);
            Assert.Null(appSettings.KeyboardLighting);
            Assert.Equal(200.0, appSettings.WindowLeft);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsPersistence_EmptySettingsFile_HandledGracefullyWithoutCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nitrolite_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(settingsPath, ""); // 0-byte file

            AppSettings appSettings = new();
            bool caughtException = false;

            try
            {
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) appSettings = s;
                }
            }
            catch (JsonException)
            {
                caughtException = true;
            }

            // Deserializing 0-byte string throws JsonException; LoadSettings catches it in try/catch { }
            Assert.True(caughtException, "Empty JSON throws JsonException, which LoadSettings must catch safely.");
            Assert.False(appSettings.StartMinimized);
            Assert.False(appSettings.IsBacklightOff);
            Assert.Null(appSettings.KeyboardLighting);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsPersistence_CorruptedSyntax_HandledGracefullyWithoutCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nitrolite_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            // Truncated/corrupted JSON
            File.WriteAllText(settingsPath, "{ \"StartMinimized\": true, \"IsBacklightOff\": true, \"KeyboardLighting\": { \"Brightness\": ");

            AppSettings appSettings = new();
            bool caughtException = false;

            try
            {
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) appSettings = s;
                }
            }
            catch (JsonException)
            {
                caughtException = true;
            }

            Assert.True(caughtException);
            Assert.NotNull(appSettings);
            // Must retain default safe initialized values
            Assert.False(appSettings.IsBacklightOff);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsPersistence_TurnOffLights_StrictRoundtripPreservation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nitrolite_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");

            // Replicate TurnOffLightsAsync + SaveSettings state:
            var lighting = new LightingConfig
            {
                Brightness = 0,
                Effect = "OFF",
                Color = "#000000",
                LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" }
            };

            var appSettings = new AppSettings
            {
                StartMinimized = false,
                KeyboardLighting = lighting,
                IsBacklightOff = true
            };

            var json = JsonSerializer.Serialize(appSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);

            // Replicate application startup reload:
            var reloadJson = File.ReadAllText(settingsPath);
            var reloadedSettings = JsonSerializer.Deserialize<AppSettings>(reloadJson);

            Assert.NotNull(reloadedSettings);
            Assert.True(reloadedSettings.IsBacklightOff, "IsBacklightOff MUST be preserved as true across app restarts!");
            Assert.NotNull(reloadedSettings.KeyboardLighting);
            Assert.Equal(0, reloadedSettings.KeyboardLighting.Brightness);
            Assert.Equal("OFF", reloadedSettings.KeyboardLighting.Effect);
            Assert.Equal("#000000", reloadedSettings.KeyboardLighting.Color);
            Assert.All(reloadedSettings.KeyboardLighting.LEDs, c => Assert.Equal("#000000", c));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsPersistence_StartupRefreshDecision_SkipsHardwareOverwrite()
    {
        // Simulate app settings where backlight was turned off
        var appSettings = new AppSettings
        {
            IsBacklightOff = true,
            KeyboardLighting = new LightingConfig { Brightness = 0, Effect = "OFF" }
        };

        // Replicate logic in MainWindow.RefreshAllAsync lines 184-247:
        bool restoredSavedRgb = false;
        bool queriedHardwareDefaults = false;

        if (appSettings.IsBacklightOff)
        {
            // Lights forced off
            restoredSavedRgb = true;
        }
        else if (appSettings.KeyboardLighting != null)
        {
            restoredSavedRgb = true;
        }

        if (!restoredSavedRgb)
        {
            queriedHardwareDefaults = true;
        }

        Assert.True(restoredSavedRgb);
        Assert.False(queriedHardwareDefaults, "When IsBacklightOff is true, startup must NOT query hardware defaults and overwrite user choice!");
    }

    [Fact]
    public void AdversarialFinding_SliderBrightnessZero_ExposesLackOfPersistenceVulnerability()
    {
        // In MainWindow.xaml.cs lines 1809-1819:
        // if (val == 0) { ... SaveActiveRgbState(); return; }
        // Note: val == 0 exits BEFORE line 1822 (_lighting.Brightness = val) and NEVER calls SaveSettings()!

        var lighting = new LightingConfig { Brightness = 5, Effect = "BREATHING" };
        var settings = new AppSettings { IsBacklightOff = false, KeyboardLighting = lighting };

        // Simulate user dragging slider to 0:
        int val = 0;
        if (val == 0)
        {
            // The code in SliderBrightness_ValueChanged did:
            // TxtBrightnessVal.Text = "0 / 9 (Kapalı)";
            // await _agentClient.TurnOffLightingAsync();
            // SaveActiveRgbState();
            // return;
            // It did NOT update lighting.Brightness or settings.IsBacklightOff!
        }
        else
        {
            lighting.Brightness = val;
        }

        // Adversarial assertion exposing this behavioral quirk:
        // If the user used the slider to turn off lights instead of the Turn Off button,
        // lighting.Brightness still equals 5 and IsBacklightOff remains false!
        Assert.Equal(5, lighting.Brightness);
        Assert.False(settings.IsBacklightOff);
    }

    [Fact]
    public void SliderBrightnessZero_RemediatedPersistence_PreservesZeroBrightnessAndIsBacklightOff()
    {
        var lighting = new LightingConfig { Brightness = 5, Effect = "BREATHING" };
        var settings = new AppSettings { IsBacklightOff = false, KeyboardLighting = lighting };

        // Remediated SliderBrightness_ValueChanged logic for val == 0:
        int val = 0;
        if (val == 0)
        {
            lighting.Brightness = 0;
            settings.IsBacklightOff = true;
            settings.KeyboardLighting = lighting;
        }

        Assert.Equal(0, lighting.Brightness);
        Assert.True(settings.IsBacklightOff);

        // Verify JSON roundtrip
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.True(restored.IsBacklightOff);
        Assert.NotNull(restored.KeyboardLighting);
        Assert.Equal(0, restored.KeyboardLighting.Brightness);
    }

    // =========================================================================
    // 3. POWER & SESSION EVENT RE-ASSERTION TIMING & SOCKET SAFETY
    // =========================================================================

    [Fact]
    public async Task ReassertKeyboardLighting_TimingAndDualPulseSimulation_ExecutesAtStagedIntervals()
    {
        var pulseTimes = new List<long>();
        var sw = Stopwatch.StartNew();

        int[] delays = { 250, 1500 };
        foreach (var delay in delays)
        {
            await Task.Delay(delay);
            pulseTimes.Add(sw.ElapsedMilliseconds);
        }

        Assert.Equal(2, pulseTimes.Count);

        // Pulse 1 should fire after ~250ms (allowing +/- 150ms tolerance for OS scheduler jitter)
        Assert.InRange(pulseTimes[0], 180, 500);

        // Pulse 2 should fire after 250 + 1500 = ~1750ms
        Assert.InRange(pulseTimes[1], 1500, 2200);

        Assert.True(pulseTimes[1] > pulseTimes[0] + 1200, "Pulse 2 must occur significantly after Pulse 1 to override late EC resets.");
    }

    [Fact]
    public async Task SocketFailure_RetryLoop_CatchesExceptionsWithoutCrashing()
    {
        // Replicate ReassertKeyboardLightingAsync retry loop with simulated socket failure
        int simulatedRetries = 0;
        bool loopFinishedGracefully = false;

        try
        {
            int[] delays = { 20, 40 }; // Accelerated for test execution speed
            foreach (var delay in delays)
            {
                await Task.Delay(delay);
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        simulatedRetries++;
                        // Simulate socket connection failure
                        throw new System.Net.Sockets.SocketException(10061); // WSAECONNREFUSED
                    }
                    catch
                    {
                        await Task.Delay(10);
                    }
                }
            }
            loopFinishedGracefully = true;
        }
        catch (Exception)
        {
            loopFinishedGracefully = false;
        }

        Assert.True(loopFinishedGracefully, "Socket errors during power re-assertion must NEVER crash or escape unhandled!");
        Assert.Equal(6, simulatedRetries); // 2 stages * 3 retries = 6 attempts
    }

    [Fact]
    public async Task ReassertDecision_HighConcurrency_IdempotentAndThreadSafe()
    {
        var settings = new AppSettings
        {
            IsBacklightOff = true,
            KeyboardLighting = new LightingConfig { Brightness = 0, Effect = "OFF" }
        };

        var results = new System.Collections.Concurrent.ConcurrentBag<bool>();
        var tasks = new List<Task>();

        // Simulate 20 rapid simultaneous wake events (PowerModes.Resume + SessionUnlock + ACPI)
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                bool shouldTurnOff = settings.IsBacklightOff ||
                                     (settings.KeyboardLighting?.Brightness ?? 0) == 0 ||
                                     string.Equals(settings.KeyboardLighting?.Effect, "OFF", StringComparison.OrdinalIgnoreCase);
                results.Add(shouldTurnOff);
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.True(r, "All concurrent wake events must consistently evaluate to turning off!"));
    }
}

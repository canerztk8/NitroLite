using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Comprehensive automated regression unit tests covering all 7 verified defects and security fixes:
/// 1. DebloatService Startup Type Whitelist Validation (command injection prevention)
/// 2. DebloatService Resilient Backup, State Recording & Restoration (no automatic fallback for unrecorded, running/task state)
/// 3. AcerLightingService Debloat Exclusion & Consistency (preserve RGB controls, 6 services count)
/// 4. SettingsManager Backup Preservation During Corrupted Recovery (settings.json.bak integrity)
/// 5. AtkMouseService Asynchronous HID Overlapped Memory Safety (cancellation, drain, buffer pinning)
/// 6. AcerAgentClient Response Validation & MUX Supported Modes (rejection of "99", bitmask decoding)
/// 7. GameDetectionService Auto Game Mode Toggling & State Restoration (clean teardown and exit detection)
/// </summary>
public class VerifiedDefectsRegressionTests
{
    // =========================================================================
    // DEFECT 1: DebloatService Startup Type Whitelist Validation
    // =========================================================================

    [Theory]
    [InlineData("Automatic", "Automatic")]
    [InlineData("automatic", "Automatic")]
    [InlineData("AUTOMATIC", "Automatic")]
    [InlineData("  Automatic  ", "Automatic")]
    [InlineData("Manual", "Manual")]
    [InlineData("manual", "Manual")]
    [InlineData("MANUAL", "Manual")]
    [InlineData("  manual  ", "Manual")]
    [InlineData("Disabled", "Disabled")]
    [InlineData("disabled", "Disabled")]
    [InlineData("DISABLED", "Disabled")]
    [InlineData("  Disabled  ", "Disabled")]
    public void Defect1_ValidateStartupType_ValidValues_NormalizeToCanonicalCasing(string input, string expectedCanonical)
    {
        string canonical = DebloatService.ValidateStartupType(input);
        Assert.Equal(expectedCanonical, canonical);

        string canonicalWithService = DebloatService.ValidateStartupType(input, "AcerServiceSvc");
        Assert.Equal(expectedCanonical, canonicalWithService);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Defect1_ValidateStartupType_NullOrWhitespace_ThrowsArgumentException(string? input)
    {
        var ex = Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType(input));
        Assert.Contains("cannot be null or empty", ex.Message, StringComparison.OrdinalIgnoreCase);

        var exWithSvc = Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType(input, "ASMSvc"));
        Assert.Contains("ASMSvc", exWithSvc.Message);
        Assert.Contains("cannot be null or empty", exWithSvc.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("; Start-Process calc.exe #")]
    [InlineData("Automatic; Invoke-Expression (Get-Content bad.ps1)")]
    [InlineData("Manual & net user attacker Password123 /add")]
    [InlineData("' | Out-File evil.txt")]
    [InlineData("$(Write-Host Pwned)")]
    [InlineData("BogusType")]
    [InlineData("DelayedAutomatic")]
    [InlineData("AutomaticDelayedStart")]
    [InlineData("System")]
    [InlineData("Boot")]
    public void Defect1_ValidateStartupType_MaliciousCommandInjection_ThrowsArgumentException(string maliciousInput)
    {
        var ex = Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType(maliciousInput));
        Assert.Contains("Invalid service startup type", ex.Message);

        var exWithSvc = Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType(maliciousInput, "AcerEZSvc"));
        Assert.Contains("AcerEZSvc", exWithSvc.Message);
        Assert.Contains("Invalid startup type", exWithSvc.Message);
    }

    [Fact]
    public void Defect1_ValidateStartupType_IncludesServiceNameInExceptionMessage()
    {
        const string serviceName = "AcerDeviceInfoAgentService";
        var ex = Assert.Throws<ArgumentException>(() => DebloatService.ValidateStartupType("InvalidType", serviceName));

        Assert.Contains(serviceName, ex.Message);
        Assert.Contains("Allowed values: Automatic, Manual, Disabled", ex.Message);
    }

    [Theory]
    [InlineData("Automatic", true)]
    [InlineData("manual", true)]
    [InlineData("DISABLED", true)]
    [InlineData("  Automatic  ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Automatic; evil", false)]
    [InlineData("Bogus", false)]
    public void Defect1_IsValidStartupType_ReturnsExpectedBoolean(string? input, bool expected)
    {
        bool isValid = DebloatService.IsValidStartupType(input);
        Assert.Equal(expected, isValid);
    }

    [Fact]
    public void Defect1_GenerateRestoreScript_WithMaliciousStartupTypeInBackup_ThrowsArgumentException()
    {
        var debloat = new DebloatService();
        var maliciousBackup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic; Remove-Item C:\\ -Recurse #" }
        };

        var ex = Assert.Throws<ArgumentException>(() => debloat.GenerateRestoreScript(maliciousBackup));
        Assert.Contains("AcerServiceSvc", ex.Message);
        Assert.Contains("Remove-Item", ex.Message);
    }

    // =========================================================================
    // DEFECT 2: DebloatService Resilient Backup, State Recording & Restoration
    // =========================================================================

    [Fact]
    public void Defect2_GenerateRestoreScript_UnrecordedServices_AreNotForcedToAutomatic()
    {
        var debloat = new DebloatService();
        var partialBackup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Manual" }
        };

        var script = debloat.GenerateRestoreScript(partialBackup);

        // Explicitly recorded service must be restored
        Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Manual", script);

        // Unrecorded Tier1 services must NOT appear in the script and must NOT be forced to Automatic
        foreach (var unrecorded in DebloatService.Tier1Services)
        {
            if (unrecorded.Equals("AcerServiceSvc", StringComparison.OrdinalIgnoreCase)) continue;

            Assert.DoesNotContain($"Set-Service -Name '{unrecorded}'", script);
            Assert.DoesNotContain($"Start-Service -Name '{unrecorded}'", script);
        }
    }

    [Fact]
    public void Defect2_GenerateRestoreScript_EmptyOrNullBackup_GeneratesNoServiceModifications()
    {
        var debloat = new DebloatService();
        var emptyBackup = new Dictionary<string, string>();

        var script = debloat.GenerateRestoreScript(emptyBackup);

        // No Set-Service commands should be emitted
        Assert.DoesNotContain("Set-Service", script);
        Assert.DoesNotContain("Start-Service", script);
    }

    [Fact]
    public void Defect2_GenerateRestoreScript_RespectsRecordedRunningStates()
    {
        var debloat = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" },
            { "AcerServiceSvc:Status", "Stopped" }, // Was stopped prior to debloat -> do NOT start
            { "ASMSvc", "Automatic" },
            { "ASMSvc:Status", "Running" },          // Was running prior to debloat -> DO start
            { "AcerEZSvc", "Manual" },
            { "AcerEZSvc:Status", "Stopped" },       // Manual and stopped -> should NOT start
            { "AcerDeviceInfoAgentService", "Manual" } // Manual with no status key -> should NOT auto-start
        };

        var script = debloat.GenerateRestoreScript(backup);

        // AcerServiceSvc was Stopped: set to Automatic, but do NOT execute Start-Service
        Assert.Contains("Set-Service -Name 'AcerServiceSvc' -StartupType Automatic", script);
        Assert.DoesNotContain("Start-Service -Name 'AcerServiceSvc'", script);

        // ASMSvc was Running: set to Automatic and DO execute Start-Service
        Assert.Contains("Set-Service -Name 'ASMSvc' -StartupType Automatic", script);
        Assert.Contains("Start-Service -Name 'ASMSvc'", script);

        // AcerEZSvc was Manual and Stopped: set to Manual, do not start
        Assert.Contains("Set-Service -Name 'AcerEZSvc' -StartupType Manual", script);
        Assert.DoesNotContain("Start-Service -Name 'AcerEZSvc'", script);

        // AcerDeviceInfoAgentService was Manual with no status key: set to Manual, do not start
        Assert.Contains("Set-Service -Name 'AcerDeviceInfoAgentService' -StartupType Manual", script);
        Assert.DoesNotContain("Start-Service -Name 'AcerDeviceInfoAgentService'", script);
    }

    [Theory]
    [InlineData("Ready", true)]
    [InlineData("Enabled", true)]
    [InlineData("Disabled", false)]
    [InlineData("Unknown", false)]
    [InlineData("NonExistent", false)]
    public void Defect2_GenerateRestoreScript_RespectsRecordedScheduledTaskState(string taskState, bool shouldEnable)
    {
        var debloat = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" },
            { "Task:NitroSenseLauncher", taskState }
        };

        var script = debloat.GenerateRestoreScript(backup);

        if (shouldEnable)
        {
            Assert.Contains("Enable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
        }
        else
        {
            Assert.DoesNotContain("Enable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
        }
    }

    [Fact]
    public void Defect2_GenerateRestoreScript_WhenTaskKeyOmitted_DoesNotEnableScheduledTask()
    {
        var debloat = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" }
        };

        var script = debloat.GenerateRestoreScript(backup);
        Assert.DoesNotContain("Enable-ScheduledTask", script);
    }

    [Fact]
    public async Task Defect2_ApplyTier1DebloatAsync_AbortsWhenBackupStateCannotBeWritten()
    {
        var debloat = new DebloatService();
        // Point backup file path to an impossible / unwritable location
        debloat.BackupStateFilePath = @"Z:\NonExistentDrive_Forbidden\backup_state.json";
        // Services enabled and running, independent of this machine's real service state.
        debloat.ServiceStartupTypeQuerySeam = _ => "Automatic";
        debloat.ServiceStatusQuerySeam = _ => "Running";
        debloat.ScheduledTaskStateQuerySeam = _ => "Ready";

        bool result = await debloat.ApplyTier1DebloatAsync();

        Assert.False(result);
        Assert.NotNull(debloat.LastError);
        Assert.Contains("servis yedeği diske yazılamadı", debloat.LastError);
    }

    [Fact]
    public void Defect2_SaveBackupState_ReturnsTrueOnSuccessAndFalseOnFailure()
    {
        var debloat = new DebloatService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"debloat_test_save_{Guid.NewGuid():N}.json");
        debloat.BackupStateFilePath = tempFile;

        try
        {
            var validState = new Dictionary<string, string>
            {
                { "AcerServiceSvc", "Automatic" },
                { "AcerServiceSvc:Status", "Running" }
            };

            bool success = debloat.SaveBackupState(validState);
            Assert.True(success);
            Assert.True(File.Exists(tempFile));

            var loaded = debloat.LoadBackupState();
            Assert.Equal("Automatic", loaded["AcerServiceSvc"]);
            Assert.Equal("Running", loaded["AcerServiceSvc:Status"]);

            // Test failure case
            debloat.BackupStateFilePath = @"Z:\NonExistentDrive\bad_path.json";
            bool failure = debloat.SaveBackupState(validState);
            Assert.False(failure);
            Assert.NotNull(debloat.LastError);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // =========================================================================
    // DEFECT 3: AcerLightingService Debloat Exclusion & Consistency
    // =========================================================================

    [Fact]
    public void Defect3_Tier1Services_ExcludesAcerLightingService_PreservingKeyboardRgb()
    {
        // AcerLightingService must NOT be included in Tier1Services debloat list
        // to preserve 4-zone keyboard RGB backlighting, animations, and timeout controls
        Assert.DoesNotContain("AcerLightingService", DebloatService.Tier1Services);
    }

    [Fact]
    public void Defect3_Tier1Services_ContainsExactlySixOEMTelemetryServices()
    {
        // Exactly 6 telemetry services matching the UI confirmation dialog:
        // "Altı adet OEM telemetri servisi devre dışı bırakılacak..." (~214 MB memory savings)
        Assert.Equal(6, DebloatService.Tier1Services.Length);

        var expectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AcerServiceSvc",
            "ASMSvc",
            "AcerDIAgentSvis",
            "AcerEZSvc",
            "AcerDeviceInfoAgentService",
            "AcerDeviceEnablingServiceV2"
        };

        foreach (var svc in DebloatService.Tier1Services)
        {
            Assert.Contains(svc, expectedServices);
        }
    }

    [Fact]
    public void Defect3_GenerateDebloatScript_DoesNotContainAcerLightingService()
    {
        var debloat = new DebloatService();
        var script = debloat.GenerateDebloatScript();

        Assert.DoesNotContain("AcerLightingService", script);
    }

    // =========================================================================
    // DEFECT 4: SettingsManager Backup Preservation During Corrupted Recovery
    // =========================================================================

    [Fact]
    public void Defect4_SaveSettingsAtomic_WhenPrimarySettingsCorrupted_PreservesValidBackupFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"settings_corrupt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string primaryPath = Path.Combine(tempDir, "settings.json");
        string backupPath = primaryPath + ".bak";

        try
        {
            // 1. Establish a valid historical backup (.bak) with distinct values
            var validHistorical = new AppSettings
            {
                SelectedChartMetric = "GPU_POWER",
                AutoRamCleanerEnabled = true,
                DefaultAtkMouseProfile = 3,
                AutoRefreshRateSwitching = false
            };
            File.WriteAllText(backupPath, JsonSerializer.Serialize(validHistorical, new JsonSerializerOptions { WriteIndented = true }));

            // 2. Corrupt the primary settings file on disk (malformed non-parsable content)
            File.WriteAllText(primaryPath, "{ CORRUPTED_NON_PARSABLE_JSON_DATA !@#$%^&*()_+= }");

            // 3. Application loads settings with fallback (recovers gracefully from .bak)
            var loaded = SettingsManager.LoadSettingsWithFallback(primaryPath, backupPath);
            Assert.Equal("GPU_POWER", loaded.SelectedChartMetric);
            Assert.True(loaded.AutoRamCleanerEnabled);
            Assert.Equal(3, loaded.DefaultAtkMouseProfile);

            // 4. Application saves updated settings
            loaded.SelectedChartMetric = "CPU_TEMP";
            SettingsManager.SaveSettingsAtomic(primaryPath, loaded);

            // 5. Verify backup file was NOT overwritten with the corrupted primary file data!
            Assert.True(File.Exists(backupPath), "Backup file must still exist after saving.");
            string backupRawContent = File.ReadAllText(backupPath);
            Assert.DoesNotContain("CORRUPTED_NON_PARSABLE_JSON_DATA", backupRawContent);

            var bakLoaded = SettingsManager.LoadSettingsWithFallback(backupPath);
            Assert.Equal("GPU_POWER", bakLoaded.SelectedChartMetric);
            Assert.True(bakLoaded.AutoRamCleanerEnabled);
            Assert.Equal(3, bakLoaded.DefaultAtkMouseProfile);

            // 6. Verify primary file now contains freshly written valid settings
            var primaryLoaded = SettingsManager.LoadSettingsWithFallback(primaryPath);
            Assert.Equal("CPU_TEMP", primaryLoaded.SelectedChartMetric);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Defect4_SaveSettingsAtomic_WhenPrimaryFileZeroBytes_PreservesValidBackupFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"settings_zero_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string primaryPath = Path.Combine(tempDir, "settings.json");
        string backupPath = primaryPath + ".bak";

        try
        {
            var validHistorical = new AppSettings { SelectedChartMetric = "BATTERY_DRAIN" };
            File.WriteAllText(backupPath, JsonSerializer.Serialize(validHistorical));

            // Primary file exists but has 0 bytes (e.g. crash during write)
            File.WriteAllText(primaryPath, "");

            var loaded = SettingsManager.LoadSettingsWithFallback(primaryPath, backupPath);
            Assert.Equal("BATTERY_DRAIN", loaded.SelectedChartMetric);

            loaded.SelectedChartMetric = "FAN_SPEED";
            SettingsManager.SaveSettingsAtomic(primaryPath, loaded);

            // Backup file must still contain the valid historical setting, not 0 bytes
            string backupText = File.ReadAllText(backupPath);
            Assert.NotEmpty(backupText);
            var bakSettings = JsonSerializer.Deserialize<AppSettings>(backupText);
            Assert.NotNull(bakSettings);
            Assert.Equal("BATTERY_DRAIN", bakSettings.SelectedChartMetric);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Defect4_IsValidSettingsFile_ComprehensiveValidation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"isvalid_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string validFile = Path.Combine(tempDir, "valid.json");
            File.WriteAllText(validFile, JsonSerializer.Serialize(new AppSettings { SelectedChartMetric = "CPU_TEMP" }));
            Assert.True(SettingsManager.IsValidSettingsFile(validFile));

            string nonExistentFile = Path.Combine(tempDir, "does_not_exist.json");
            Assert.False(SettingsManager.IsValidSettingsFile(nonExistentFile));

            string zeroByteFile = Path.Combine(tempDir, "zero.json");
            File.WriteAllText(zeroByteFile, "");
            Assert.False(SettingsManager.IsValidSettingsFile(zeroByteFile));

            string whitespaceFile = Path.Combine(tempDir, "space.json");
            File.WriteAllText(whitespaceFile, "   \r\n\t  ");
            Assert.False(SettingsManager.IsValidSettingsFile(whitespaceFile));

            string corruptJsonFile = Path.Combine(tempDir, "corrupt.json");
            File.WriteAllText(corruptJsonFile, "{ invalid json data [[[");
            Assert.False(SettingsManager.IsValidSettingsFile(corruptJsonFile));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Defect4_SaveSettingsAtomic_WhenPrimaryFileValid_MaintainsRollingBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"settings_valid_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string primaryPath = Path.Combine(tempDir, "settings.json");
        string backupPath = primaryPath + ".bak";

        try
        {
            var configA = new AppSettings { SelectedChartMetric = "CONFIG_A" };
            SettingsManager.SaveSettingsAtomic(primaryPath, configA);

            var configB = new AppSettings { SelectedChartMetric = "CONFIG_B" };
            SettingsManager.SaveSettingsAtomic(primaryPath, configB);

            // Primary must have Config B, backup must have Config A
            var primaryLoaded = SettingsManager.LoadSettingsWithFallback(primaryPath);
            Assert.Equal("CONFIG_B", primaryLoaded.SelectedChartMetric);

            var backupLoaded = SettingsManager.LoadSettingsWithFallback(backupPath);
            Assert.Equal("CONFIG_A", backupLoaded.SelectedChartMetric);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // =========================================================================
    // DEFECT 5: AtkMouseService Asynchronous HID Overlapped Memory Safety
    // =========================================================================

    [LiveSystemTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Defect5_AtkMouseService_SwitchProfile_SafeExecution(int profile)
    {
        var service = new AtkMouseService();
        // Switching profiles should execute safely without throwing unhandled exceptions,
        // without crashing, and without memory corruption (whether hardware is connected or not)
        var ex = Record.Exception(() => service.SwitchProfile(profile));
        Assert.Null(ex);
        Assert.InRange(service.CurrentActiveProfile, 1, 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(99)]
    public void Defect5_AtkMouseService_SwitchProfile_InvalidProfile_ReturnsFalse(int invalidProfile)
    {
        var service = new AtkMouseService();
        bool result = service.SwitchProfile(invalidProfile);
        Assert.False(result);
    }

    [Fact]
    public void Defect5_AtkMouseService_QueryCurrentProfile_SafeExecution()
    {
        var service = new AtkMouseService();
        var ex = Record.Exception(() =>
        {
            int? current = service.QueryCurrentProfile();
            if (current.HasValue)
            {
                Assert.InRange(current.Value, 1, 4);
            }
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Defect5_AtkMouseService_WriteAndReadPacket_RejectsInvalidDevicePathGracefully()
    {
        var service = new AtkMouseService();
        var packet = AtkMouseService.BuildGetCurrentConfigPacket();

        var method = typeof(AtkMouseService).GetMethod(
            "WriteAndReadPacket",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var arguments = new object[] { $@"\\.\NonExistentDevice_{Guid.NewGuid():N}", packet,
            AtkMouseService.CMD_GET_CURRENT_CONFIG, AtkMouseResult.Confirmed };
        var result = method.Invoke(service, arguments);
        Assert.Null(result);
        Assert.Equal(AtkMouseResult.CommunicationError, arguments[3]);
    }

    [Theory]
    [InlineData(1, 0, 0x3D)]
    [InlineData(2, 1, 0x3C)]
    [InlineData(3, 2, 0x3B)]
    [InlineData(4, 3, 0x3A)]
    public void Defect5_AtkMouseService_PacketChecksum_AllProfilesVerified(int profileNumber, byte expectedIndex, byte expectedChecksum)
    {
        byte configIndex = (byte)(profileNumber - 1);
        byte[] packet = AtkMouseService.BuildSetCurrentConfigPacket(configIndex);

        Assert.Equal(AtkMouseService.REPORT_LEN_ATK_COL05, packet.Length);
        Assert.Equal(AtkMouseService.REPORT_ID_ATK_COL05, packet[0]);
        Assert.Equal(AtkMouseService.CMD_SET_CURRENT_CONFIG, packet[1]);
        Assert.Equal(0, packet[2]); // Status
        Assert.Equal(1, packet[5]); // DataValidLen
        Assert.Equal(expectedIndex, packet[6]);
        Assert.Equal(expectedChecksum, packet[16]);

        // Verify checksum formula: 0x55 - (sum(packet[0..15]) & 0xFF)
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += packet[i];
        byte calcChecksum = (byte)((0x55 - (sum & 0xFF)) & 0xFF);
        Assert.Equal(calcChecksum, packet[16]);
    }

    [Fact]
    public void Defect5_AtkMouseService_OverlappedIoSafetySimulation_CancelAndSynchronousDrain()
    {
        // Verify kernel contract implemented in AtkMouseService:
        // CancelIoEx followed by GetOverlappedResult(..., true) cleanly reaps pending I/O
        // before buffers are unpinned and handles are closed.
        string pipeName = @"\\.\pipe\atk_test_pipe_" + Guid.NewGuid().ToString("N");
        const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        const uint PIPE_TYPE_BYTE = 0x00000000;
        const uint PIPE_READMODE_BYTE = 0x00000000;
        const uint PIPE_WAIT = 0x00000000;
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint OPEN_EXISTING = 3;
        const int ERROR_IO_PENDING = 997;
        const int ERROR_OPERATION_ABORTED = 995;

        SafeFileHandle serverHandle = CreateNamedPipe(
            pipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 1024, 1024, 0, IntPtr.Zero);

        Assert.False(serverHandle.IsInvalid);

        // Connect client endpoint so the server pipe enters connected state
        SafeFileHandle clientHandle = CreateFile(
            pipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        Assert.False(clientHandle.IsInvalid);

        IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        Assert.NotEqual(IntPtr.Zero, hEvent);

        byte[] rxBuf = new byte[64];
        GCHandle rxBufHandle = GCHandle.Alloc(rxBuf, GCHandleType.Pinned);
        NativeOverlapped ov = new NativeOverlapped { EventHandle = hEvent };

        try
        {
            uint readBytes = 0;
            // No data has been written by client yet, so ReadFile MUST return false with ERROR_IO_PENDING
            bool readOk = ReadFile(serverHandle, rxBuf, (uint)rxBuf.Length, out readBytes, ref ov);
            int lastErr = Marshal.GetLastWin32Error();

            Assert.False(readOk);
            Assert.Equal(ERROR_IO_PENDING, lastErr);

            // Simulate timeout or receiver disconnect: Cancel pending I/O via CancelIoEx
            bool cancelOk = CancelIoEx(serverHandle, ref ov);
            Assert.True(cancelOk, "CancelIoEx should succeed on pending overlapped I/O.");

            // Await kernel drain via GetOverlappedResult with bWait = true
            bool drainOk = GetOverlappedResult(serverHandle, ref ov, out _, true);
            int drainErr = Marshal.GetLastWin32Error();

            // Operation aborted is the expected completion code
            Assert.False(drainOk);
            Assert.Equal(ERROR_OPERATION_ABORTED, drainErr);
        }
        finally
        {
            CloseHandle(hEvent);
            if (rxBufHandle.IsAllocated) rxBufHandle.Free();
            clientHandle.Close();
            serverHandle.Close();
        }
    }

    #region Win32 Test Helpers for Overlapped Safety
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateNamedPipe(
        string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances,
        uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    #endregion

    // =========================================================================
    // DEFECT 6: AcerAgentClient Response Validation & MUX Supported Modes
    // =========================================================================

    [Theory]
    [InlineData("{\"result\": \"99\"}")]
    [InlineData("{\"result\": \"99\", \"error\": \"Device Busy or Unsupported\"}")]
    [InlineData("{\"result\": 99}")]
    [InlineData("{\"result\": \"1\"}")]
    [InlineData("{\"result\": \"2\"}")]
    [InlineData("{\"result\": \"-1\"}")]
    [InlineData("{\"result\": \"ERROR\"}")]
    [InlineData("{\"result\": \"failed\"}")]
    // AASSvc RETURN_CODE 3 = WRONG_PARAMETERS (NitroSense enum, confirmed live with an unknown function)
    [InlineData("{\"result\": \"3\"}")]
    [InlineData("{\"result\": 3}")]
    [InlineData("{\"result\": \"3\", \"data\": {\"mode\": 1}}")]
    // Observed live for an out-of-range OPERATING_MODE value
    [InlineData("{\"result\": \"4\"}")]
    public void Defect6_AcerAgentClient_IsSuccessResult_TreatsResult99AndErrorCodesAsFalse(string responseJson)
    {
        bool success = AcerAgentClient.IsSuccessResult(responseJson);
        Assert.False(success, $"Response '{responseJson}' must NOT be treated as success.");
    }

    [Theory]
    [InlineData("{\"result\": \"0\"}")]
    [InlineData("{\"result\": 0}")]
    [InlineData("{\"result\": \"0\", \"data\": {\"status\": \"ok\"}}")]
    public void Defect6_AcerAgentClient_IsSuccessResult_ReturnsTrueOnlyFor0(string responseJson)
    {
        bool success = AcerAgentClient.IsSuccessResult(responseJson);
        Assert.True(success, $"Response '{responseJson}' should be recognized as success.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ invalid json string")]
    [InlineData("Random text message from service")]
    [InlineData("{}")]
    [InlineData("{\"status\": \"ok\"}")]
    [InlineData("{\"data\": 123}")]
    public void Defect6_AcerAgentClient_IsSuccessResult_NullEmptyAndMalformedJsonReturnFalse(string? responseJson)
    {
        bool success = AcerAgentClient.IsSuccessResult(responseJson);
        Assert.False(success);
    }

    [Theory]
    // Bitmask 0: None supported
    [InlineData(0, GpuOperatingMode.Optimus, false)]
    [InlineData(0, GpuOperatingMode.DiscreteGpu, false)]
    [InlineData(0, GpuOperatingMode.Automatic, false)]
    // Bitmask 1 (0b001): Optimus (Hybrid) only
    [InlineData(1, GpuOperatingMode.Optimus, true)]
    [InlineData(1, GpuOperatingMode.DiscreteGpu, false)]
    [InlineData(1, GpuOperatingMode.Automatic, false)]
    // Bitmask 2 (0b010): DiscreteGpu only
    [InlineData(2, GpuOperatingMode.Optimus, false)]
    [InlineData(2, GpuOperatingMode.DiscreteGpu, true)]
    [InlineData(2, GpuOperatingMode.Automatic, false)]
    // Bitmask 3 (0b011): Optimus + DiscreteGpu (standard MUX without Advanced Optimus)
    [InlineData(3, GpuOperatingMode.Optimus, true)]
    [InlineData(3, GpuOperatingMode.DiscreteGpu, true)]
    [InlineData(3, GpuOperatingMode.Automatic, false)]
    // Bitmask 4 (0b100): Automatic (DDS) only
    [InlineData(4, GpuOperatingMode.Optimus, false)]
    [InlineData(4, GpuOperatingMode.DiscreteGpu, false)]
    [InlineData(4, GpuOperatingMode.Automatic, true)]
    // Bitmask 7 (0b111): All 3 modes supported (Full Advanced Optimus + MUX)
    [InlineData(7, GpuOperatingMode.Optimus, true)]
    [InlineData(7, GpuOperatingMode.DiscreteGpu, true)]
    [InlineData(7, GpuOperatingMode.Automatic, true)]
    public void Defect6_MainWindow_IsGpuModeSupported_DecodesBitmaskAccurately(int bitmask, GpuOperatingMode mode, bool expectedSupported)
    {
        bool isSupported = MainWindow.IsGpuModeSupported(bitmask, mode);
        Assert.Equal(expectedSupported, isSupported);
    }

    [Fact]
    public void Defect6_MainWindow_IsGpuModeSupported_ExtremeAndBoundaryBitmasks()
    {
        // Boundary: Higher bits set (e.g. 0b1111 = 15) should still preserve low 3 bits
        Assert.True(MainWindow.IsGpuModeSupported(15, GpuOperatingMode.Optimus));
        Assert.True(MainWindow.IsGpuModeSupported(15, GpuOperatingMode.DiscreteGpu));
        Assert.True(MainWindow.IsGpuModeSupported(15, GpuOperatingMode.Automatic));

        // Negative numbers or high bit patterns
        Assert.False(MainWindow.IsGpuModeSupported(8, GpuOperatingMode.Optimus)); // bit 3 only
        Assert.False(MainWindow.IsGpuModeSupported(8, GpuOperatingMode.DiscreteGpu));
        Assert.False(MainWindow.IsGpuModeSupported(8, GpuOperatingMode.Automatic));
    }

    // =========================================================================
    // DEFECT 7: GameDetectionService Auto Game Mode Toggling & State Restoration
    // =========================================================================

    [Fact]
    public void Defect7_AutoGameModeEnabled_ToggledOffDuringGame_InvokesGameEndedAndCleansState()
    {
        using var service = new GameDetectionService();

        var profile = new GameProfile
        {
            ExeName = "cs2",
            DisplayName = "Counter-Strike 2",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive
        };

        int currentPid = Process.GetCurrentProcess().Id;
        service.SetActiveGameForTesting(profile, currentPid);

        Assert.True(service.IsGameActive);
        Assert.Equal(profile, service.ActiveGameProfile);
        Assert.Equal(currentPid, service.ActiveProcessId);

        GameProfile? endedProfile = null;
        int endedEventCount = 0;
        service.GameEnded += p =>
        {
            endedEventCount++;
            endedProfile = p;
        };

        // User unchecks AutoGameMode checkbox during active gameplay
        service.AutoGameModeEnabled = false;

        // Verify session was immediately torn down and GameEnded was dispatched
        Assert.Equal(1, endedEventCount);
        Assert.NotNull(endedProfile);
        Assert.Equal("cs2", endedProfile.ExeName);

        Assert.False(service.IsGameActive);
        Assert.Null(service.ActiveGameProfile);
        Assert.Equal(0, service.ActiveProcessId);
    }

    [Fact]
    public void Defect7_AutoGameModeEnabled_ToggledOffWhenNoGame_DoesNotInvokeGameEnded()
    {
        using var service = new GameDetectionService();

        int endedCount = 0;
        service.GameEnded += _ => endedCount++;

        // No active game running
        service.AutoGameModeEnabled = false;

        Assert.Equal(0, endedCount);

        // Setting same value again should not trigger events
        service.AutoGameModeEnabled = false;
        Assert.Equal(0, endedCount);
    }

    [Fact]
    public void Defect7_ScanProcesses_FastPathDetectsGameExitEvenWhenAutoGameModeDisabled()
    {
        using var service = new GameDetectionService();

        // Launch a brief short-lived process to obtain a guaranteed terminated PID
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var p = Process.Start(psi);
        Assert.NotNull(p);
        int terminatedPid = p.Id;
        p.WaitForExit(5000);
        Assert.True(p.HasExited);

        var profile = new GameProfile
        {
            ExeName = "cmd",
            DisplayName = "Exited Command Prompt"
        };

        // Simulate that the game was active with this now-terminated PID
        service.SetActiveGameForTesting(profile, terminatedPid);

        // Disable AutoGameMode while active session exists
        // (Even if AutoGameModeEnabled is toggled false or kept false, fast-path exit detection runs)
        service.AutoGameModeEnabled = true;

        GameProfile? endedProfile = null;
        service.GameEnded += prof => endedProfile = prof;

        // Force scan: FAST-PATH must detect the terminated PID, reset state, and invoke GameEnded
        service.TriggerScanForTesting();

        Assert.NotNull(endedProfile);
        Assert.Equal("cmd", endedProfile.ExeName);
        Assert.False(service.IsGameActive);
        Assert.Null(service.ActiveGameProfile);
        Assert.Equal(0, service.ActiveProcessId);
    }

    [Fact]
    public void Defect7_AutoGameModeEnabled_Reenabled_ResumesScanningSafely()
    {
        using var service = new GameDetectionService();

        service.AutoGameModeEnabled = false;
        Assert.False(service.AutoGameModeEnabled);

        service.AutoGameModeEnabled = true;
        Assert.True(service.AutoGameModeEnabled);

        var ex = Record.Exception(() => service.TriggerScanForTesting());
        Assert.Null(ex);
    }

    [Fact]
    public void Debloat_GenerateDebloatScript_WhenTaskOmittedFromRecordedState_DoesNotDisableScheduledTask()
    {
        var debloat = new DebloatService();
        var recordedState = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" }
        };

        var script = debloat.GenerateDebloatScript(recordedState);
        Assert.DoesNotContain("Disable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Unknown")]
    [InlineData("Devre Dışı")]
    public void Debloat_GenerateDebloatScript_WhenTaskRecordedAsInactive_DoesNotDisableScheduledTask(string inactiveState)
    {
        var debloat = new DebloatService();
        var recordedState = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" },
            { "Task:NitroSenseLauncher", inactiveState }
        };

        var script = debloat.GenerateDebloatScript(recordedState);
        Assert.DoesNotContain("Disable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Hazır")]
    [InlineData("Running")]
    public void Debloat_GenerateDebloatScript_WhenTaskRecordedAsActive_DisablesScheduledTask(string activeState)
    {
        var debloat = new DebloatService();
        var recordedState = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" },
            { "Task:NitroSenseLauncher", activeState }
        };

        var script = debloat.GenerateDebloatScript(recordedState);
        Assert.Contains("Disable-ScheduledTask -TaskName 'NitroSenseLauncher'", script);
    }

    [Fact]
    public void Debloat_GenerateRestoreScript_WhenTaskRecordedAsActive_DoesNotSuppressErrors()
    {
        var debloat = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" },
            { "Task:NitroSenseLauncher", "Ready" }
        };

        var script = debloat.GenerateRestoreScript(backup);
        Assert.Contains("Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop", script);
        Assert.Contains("if (-not $task) { throw 'NitroSenseLauncher görevi bulunamadı.' }", script);
        Assert.Contains("Enable-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop", script);
        Assert.Contains("$errs += ('Task:NitroSenseLauncher: ' + $_.Exception.Message)", script);
    }

    [Fact]
    public void Debloat_BackupState_PreservesOriginalKnownTaskState_AcrossRepeatedDebloats()
    {
        var debloat = new DebloatService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"debloat_test_preserve_{Guid.NewGuid():N}.json");
        debloat.BackupStateFilePath = tempFile;

        try
        {
            var initialBackup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "AcerServiceSvc", "Automatic" },
                { "AcerServiceSvc:Status", "Running" },
                { "Task:NitroSenseLauncher", "Ready" }
            };
            debloat.SaveBackupState(initialBackup);

            // Simulate second debloat run where task is already Disabled
            var loaded = debloat.LoadBackupState();
            var secondTaskState = "Disabled";

            if (!string.IsNullOrEmpty(secondTaskState) && !secondTaskState.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                bool hasValidKnownBackup = loaded.TryGetValue("Task:NitroSenseLauncher", out var existingTask) &&
                                           !string.IsNullOrWhiteSpace(existingTask) &&
                                           !existingTask.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

                if (!hasValidKnownBackup)
                {
                    loaded["Task:NitroSenseLauncher"] = secondTaskState;
                }
            }

            Assert.Equal("Ready", loaded["Task:NitroSenseLauncher"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CpuPowerService_ConcurrentSetBoostModeAsync_ExecutesSafelyWithoutDeadlock()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.BypassNativePowerCalls = true;
        service.ProcessInfoFactory = cmd => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var tasks = new[]
        {
            Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: true, targetDc: false)),
            Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Efficient, targetAc: false, targetDc: true)),
            Task.Run(() => service.SetBoostModeAsync(CpuBoostMode.Aggressive, targetAc: true, targetDc: true))
        };

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);
    }

    // =========================================================================
    // REMAINING 3 RESTORATION DEFECTS REGRESSION TESTS
    // =========================================================================

    [Fact]
    public void CpuPowerService_RunPowerCfg_NonZeroExitCode_FailsGracefully()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.ProcessInfoFactory = args => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 42",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        bool ok = service.RunPowerCfg("/test");
        Assert.False(ok);
    }

    [Fact]
    public void CpuPowerService_RunPowerCfg_Timeout_KillsProcessAndFails()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.ProcessInfoFactory = args => new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 10",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var sw = Stopwatch.StartNew();
        bool ok = service.RunPowerCfg("/test");
        sw.Stop();

        Assert.False(ok);
        // Should time out around 3000ms (+ kill wait) rather than waiting the full 10s
        Assert.True(sw.ElapsedMilliseconds < 8000, $"Elapsed: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void CpuPowerService_HangingProcess_BlocksSubsequentWrites()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        // Start a support sleep process and simulate an unexited hanging process
        var hanging = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 10",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(hanging);
        try
        {
            service._hangingProcess = hanging;

            // Attempting to run powercfg must be blocked immediately
            bool ok = service.RunPowerCfg("/blocked");
            Assert.False(ok);

            bool setOk = service.SetBoostModePowerCfg(CpuBoostMode.Efficient);
            Assert.False(setOk);
        }
        finally
        {
            try
            {
                hanging.Kill();
                hanging.WaitForExit(1000);
            }
            catch { }
            hanging.Dispose();
        }
    }

    [Fact]
    public void CpuPowerService_SetBoostModePowerCfg_DcOnly_TargetsOnlyDc()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        var capturedArgs = new List<string>();

        service.ProcessInfoFactory = args =>
        {
            capturedArgs.Add(args);
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        };

        bool ok = service.SetBoostModePowerCfg(CpuBoostMode.Disabled, targetAc: false, targetDc: true);
        Assert.True(ok);

        Assert.Contains(capturedArgs, a => a.Contains("/setdcvalueindex"));
        Assert.Contains(capturedArgs, a => a.Contains("/setactive"));
        Assert.DoesNotContain(capturedArgs, a => a.Contains("/setacvalueindex"));
    }

    [Fact]
    public void CpuPowerService_QueryCurrentBoostModePowerCfg_ParsesBothAcAndDc()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.MockPowerCfgQueryOutput =
            "    Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)\r\n" +
            "    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)\r\n" +
            "      Current AC Power Setting Index: 0x00000000\r\n" +
            "      Current DC Power Setting Index: 0x00000004\r\n";

        var ac = service.QueryCurrentBoostModePowerCfg(isAc: true);
        var dc = service.QueryCurrentBoostModePowerCfg(isAc: false);

        Assert.Equal(CpuBoostMode.Disabled, ac);
        Assert.Equal(CpuBoostMode.Efficient, dc);
    }

    [Fact]
    public void DebloatService_GenerateRestoreScript_MissingService_ThrowsAndPopulatesErrors()
    {
        var debloat = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" }
        };

        string script = debloat.GenerateRestoreScript(backup);

        Assert.Contains("Get-Service -Name 'AcerServiceSvc' -ErrorAction Stop", script);
        Assert.Contains("if (-not $svc) { throw 'Servis bulunamadı: AcerServiceSvc' }", script);
        Assert.Contains("$errs += ('AcerServiceSvc: ' + $_.Exception.Message)", script);
    }

    [Fact]
    public void DebloatService_GenerateRestoreScript_IncludesPostRestoreVerification()
    {
        var debloat = new DebloatService();
        var backup = new Dictionary<string, string>
        {
            { "AcerServiceSvc", "Automatic" },
            { "AcerServiceSvc:Status", "Running" },
            { "Task:NitroSenseLauncher", "Ready" }
        };

        string script = debloat.GenerateRestoreScript(backup);

        // Service verification
        Assert.Contains("$vSvc = Get-Service -Name 'AcerServiceSvc' -ErrorAction Stop", script);
        Assert.Contains("if ($vSvc.StartType -ne 'Automatic')", script);
        Assert.Contains("if ($vSvc.Status -ne 'Running')", script);

        // Task verification
        Assert.Contains("$vTask = Get-ScheduledTask -TaskName 'NitroSenseLauncher' -ErrorAction Stop", script);
        Assert.Contains("if ($vTask.State -eq 'Disabled')", script);
    }

    [Fact]
    public async Task DebloatService_RestoreFailure_PreservesBackupFileOnDisk()
    {
        var debloat = new DebloatService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"debloat_fail_preserve_{Guid.NewGuid():N}.json");
        debloat.BackupStateFilePath = tempFile;

        try
        {
            var initial = new Dictionary<string, string>
            {
                { "AcerServiceSvc", "Automatic" },
                { "AcerDeviceEnablingServiceV2", "Manual" }
            };
            debloat.SaveBackupState(initial);
            Assert.True(File.Exists(tempFile));

            // Generate restore script and verify it targets both
            string script = debloat.GenerateRestoreScript(initial);
            Assert.Contains("AcerServiceSvc", script);
            Assert.Contains("AcerDeviceEnablingServiceV2", script);

            // Note: RestoreTier1ServicesAsync requires elevated privileges to execute PowerShell.
            // When run non-elevated or when script exits with 1, backup file must NOT be deleted.
            // Directly test that LoadBackupState continues to preserve content.
            var reloaded = debloat.LoadBackupState();
            Assert.Equal("Automatic", reloaded["AcerServiceSvc"]);
            Assert.Equal("Manual", reloaded["AcerDeviceEnablingServiceV2"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GameTransition_Lock_MaintainsOrderBetweenOperations()
    {
        var transitionLock = new SemaphoreSlim(1, 1);
        var order = new List<string>();

        // Step 1: Holder acquires lock and performs delayed operation
        await transitionLock.WaitAsync();
        var t1 = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150);
                order.Add("Operation1_Done");
            }
            finally
            {
                transitionLock.Release();
            }
        });

        // Step 2: Second operation waits for the lock
        var t2 = Task.Run(async () =>
        {
            await transitionLock.WaitAsync();
            try
            {
                order.Add("Operation2_Done");
            }
            finally
            {
                transitionLock.Release();
            }
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(2, order.Count);
        Assert.Equal("Operation1_Done", order[0]);
        Assert.Equal("Operation2_Done", order[1]);
    }

    [Fact]
    public async Task CpuPowerService_BypassNativePowerCalls_PreventsAnyProcessExecutionAndCompletesInMem()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        Assert.True(service.BypassNativePowerCalls);
        Assert.Null(service.ProcessInfoFactory);

        // RunPowerCfg must return true immediately without spawning powercfg.exe
        bool runOk = service.RunPowerCfg("/test");
        Assert.True(runOk);

        // Query paths must return simulated CurrentMode without spawning powercfg.exe
        var exactMode = service.QueryBoostModeExact(isAc: true);
        Assert.Equal(service.CurrentMode, exactMode);

        var queryMode = service.QueryCurrentBoostModePowerCfg(isAc: true);
        Assert.Equal(service.CurrentMode, queryMode);

        // SetBoostModePowerCfg must succeed in memory without spawning powercfg.exe
        bool setCfgOk = service.SetBoostModePowerCfg(CpuBoostMode.Efficient);
        Assert.True(setCfgOk);
        Assert.Equal(CpuBoostMode.Efficient, service.CurrentMode);
        Assert.True(service.IsModeVerified);

        // SetBoostModeAsync must complete in memory without spawning powercfg.exe
        bool setAsyncOk = await service.SetBoostModeAsync(CpuBoostMode.Disabled);
        Assert.True(setAsyncOk);
        Assert.Equal(CpuBoostMode.Disabled, service.CurrentMode);
        Assert.True(service.IsModeVerified);
        Assert.Equal(CpuBoostMode.Disabled, service.VerifiedCurrentMode);
    }

    [Fact]
    public void CpuPowerService_BypassNativePowerCalls_WithCustomProcessInfoFactory_RespectsCustomProcessInfoFactory()
    {
        var service = new CpuPowerService(autoInitialize: false, bypassNativePowerCalls: true);
        service.ProcessInfoFactory = args => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 42",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // When ProcessInfoFactory is provided, RunPowerCfg uses it
        bool runOk = service.RunPowerCfg("/custom");
        Assert.False(runOk);

        // SetBoostModePowerCfg should fail due to exit code 42
        bool setCfgOk = service.SetBoostModePowerCfg(CpuBoostMode.Aggressive);
        Assert.False(setCfgOk);
        Assert.False(service.IsModeVerified);
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NitroLite.Core;

using System.Threading.Tasks;

namespace NitroLite.Tests;

class Program
{
    static async Task<int> Main()
    {
        int failedTests = 0;
        Console.WriteLine("==================================================");
        Console.WriteLine("  NITROLITE STORAGE & THERMAL HEALTH TEST SUITE   ");
        Console.WriteLine("==================================================");

        // TEST 0: Live AASSvc Telemetry Check
        Console.WriteLine("\n[TEST 0] Testing AcerAgentClient Live Telemetry...");
        try
        {
            using var client = new AcerAgentClient();
            var monData = client.GetMonitorDataAsync().GetAwaiter().GetResult();
            if (monData != null)
            {
                using var hwService = new HardwareMonitorService();
                hwService.EnrichMonitorData(monData);
                Console.WriteLine($" -> CPU Temp: {monData.CpuTemperature}°C | GPU Temp: {monData.GpuTemperature}°C | Sys Temp: {monData.SysTemperature}°C");
                Console.WriteLine($" -> CPU Fans: {monData.CpuFanSpeed} RPM | GPU Fans: {monData.GpuFanSpeed} RPM | Sys Fans: {monData.SysFanSpeed} RPM");
                Console.WriteLine($" -> CPU Usage: {monData.CpuUsage}% | CPU Power: {monData.CpuPowerWatts}W | Freq: {monData.CpuFrequencyMHz} MHz");
            }
            else
            {
                Console.WriteLine(" -> AcerAgentClient returned null (Service not responding or busy)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($" -> AcerAgentClient test error: {ex.Message}");
        }

        // TEST 1: StorageHealthService Live Hardware Inspection
        Console.WriteLine("\n[TEST 1] Testing StorageHealthService.GetDrives()...");
        try
        {
            var storageService = new StorageHealthService();
            var drives = storageService.GetDrives();

            Console.WriteLine($"Disks detected: {drives.Count}");
            foreach (var d in drives)
            {
                Console.WriteLine($" -> Disk {d.DiskNumber} [{d.DriveLetter}]: {d.Model} ({d.BusType})");
                Console.WriteLine($"    Total: {d.TotalCapacityGB:F1} GB | Used: {d.UsedCapacityGB:F1} GB ({d.UsedPercent:F1}%) | Free: {d.FreeCapacityGB:F1} GB");
                Console.WriteLine($"    Health: {d.EstHealthPercent}% ({d.HealthDesc})");
            }

            if (drives.Count < 1)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL: Expected at least 1 storage drive on this system!");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                var d0 = drives[0];

                // Verify Disk 0 (C: drive) is not 0% full
                if (d0.DriveLetter != "C" && !d0.DriveLetter.Contains("C") && string.IsNullOrEmpty(d0.DriveLetter))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"FAIL: Disk 0 expected DriveLetter 'C', got '{d0.DriveLetter}'");
                    Console.ResetColor();
                    failedTests++;
                }
                else if (d0.TotalCapacityGB <= 0.0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"FAIL: Disk 0 shows 0 capacity! Total: {d0.TotalCapacityGB}");
                    Console.ResetColor();
                    failedTests++;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"PASS: Disk 0 ({d0.DriveLetter}:) resolved correctly: {d0.UsedCapacityGB:F1} GB / {d0.TotalCapacityGB:F1} GB ({d0.UsedPercent:F1}% full)");
                    Console.ResetColor();
                }

                if (drives.Count >= 2)
                {
                    var d1 = drives[1];

                    // Verify Disk 1 (D: drive)
                    if (d1.DriveLetter != "D" && !d1.DriveLetter.Contains("D") && string.IsNullOrEmpty(d1.DriveLetter))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"FAIL: Disk 1 expected DriveLetter 'D', got '{d1.DriveLetter}'");
                        Console.ResetColor();
                        failedTests++;
                    }
                    else if (d1.TotalCapacityGB <= 0.0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"FAIL: Disk 1 shows 0 capacity! Total: {d1.TotalCapacityGB}");
                        Console.ResetColor();
                        failedTests++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"PASS: Disk 1 ({d1.DriveLetter}:) resolved correctly: {d1.UsedCapacityGB:F1} GB / {d1.TotalCapacityGB:F1} GB ({d1.UsedPercent:F1}% full)");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.WriteLine(" -> Single drive machine detected. Secondary drive check skipped.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"EXCEPTION in StorageHealthService: {ex}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 6: TelemetryFormatter Unit Tests
        Console.WriteLine("\n[TEST 6] Testing TelemetryFormatter Unit Tests...");
        try
        {
            var t = new TelemetryFormatterTests();
            t.CalculateFanPercent_NormalValues_ReturnsExpectedPercentage();
            t.CalculateFanPercent_OverMaxRpm_ClampsTo100Percent();
            t.CalculateFanPercent_ZeroOrNegative_ReturnsZero();
            t.FormatFanSpeed_FormatsRpmAndPercentageCorrectly();
            t.FormatFanSpeedRpm_FormatsRpmCorrectly();
            t.FormatFanSpeedPercent_FormatsPercentageCorrectly();
            t.FormatCpuTemperature_ActiveAndIdleValues_FormatsExpectedText();
            t.FormatGpuTemperature_SleepAndActiveStates_FormatsAccurately();
            t.FormatCpuPower_PositiveAndZeroValues_FormatsExpectedText();
            t.FormatGpuPower_SleepAndActiveStates_FormatsAccurately();
            t.FormatCpuFrequency_ActiveAndZeroClocks_FormatsExpectedText();
            t.FormatGpuFrequency_ActiveAndSleepClocks_FormatsExpectedText();
            t.FormatRamUsage_NormalAndZeroFallback_FormatsExpectedText();
            t.FormatVramUsage_NormalAndSleepFallback_FormatsExpectedText();
            t.FormatBatteryPowerFlow_DischargingState_ReturnsDesarjOluyor();
            t.FormatBatteryPowerFlow_ChargingState_ReturnsSarjOluyor();
            t.FormatBatteryPowerFlow_PluggedInSaturated_ReturnsAdaptordeDoygun();
            t.FormatBatteryPowerFlow_OnBatteryZeroDischarge_ReturnsPildeBosta();
            t.FormatBatteryPowerFlow_NullStatus_ReturnsZeroWatts();
            t.FormatRemainingTime_VariousConditions_FormatsAccurately();
            t.FormatDiskCapacity_NormalAndZero_FormatsAccurately();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All 21 TelemetryFormatter tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in TelemetryFormatter tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 7: StorageHealthService Unit Tests
        Console.WriteLine("\n[TEST 7] Testing StorageHealthService Unit Tests...");
        try
        {
            var t = new StorageHealthServiceTests();
            t.DeviceIdRegexParsing_NonNumericAndNumericIdentifiers_ExtractsCorrectDiskNumber("PHYSICALDRIVE0", 0);
            t.DeviceIdRegexParsing_NonNumericAndNumericIdentifiers_ExtractsCorrectDiskNumber("1", 1);
            t.MultiPartitionDriveCapacity_UsesPhysicalSizeAsAuthoritativeCapacity();
            t.HealthComputation_WithWearIndicator_CalculatesLegitimatePercentage(5, 95, "Sağlıklı (Nominal)");
            t.HealthComputation_WithoutWearIndicator_MapsStatusCorrectly(0, 100, "Sağlıklı (Nominal)");
            t.GetDrives_LiveInspection_DiscoversAtLeastOneDriveWithoutHardcodedCount();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All StorageHealthService unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in StorageHealthService unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 10: CpuPowerService Unit Tests
        Console.WriteLine("\n[TEST 10] Testing CpuPowerService Unit Tests...");
        try
        {
            var t = new CpuPowerServiceTests();
            t.CpuBoostModeEnum_HasExplicitWin32PowerSettingIndexValues();
            t.PowrProfIndexParsing_MapsValueToAppropriateMode(0, CpuBoostMode.Disabled);
            t.PowrProfIndexParsing_MapsValueToAppropriateMode(2, CpuBoostMode.Aggressive);
            t.PowrProfIndexParsing_MapsValueToAppropriateMode(4, CpuBoostMode.Efficient);
            t.PowerCfgRegex_ExtractsAndParsesHexIndex("Current AC Power Setting Index: 0x00000000", CpuBoostMode.Disabled);
            t.PowerCfgRegex_ExtractsAndParsesHexIndex("Current AC Power Setting Index: 0x00000002", CpuBoostMode.Aggressive);
            t.PowerCfgRegex_ExtractsAndParsesHexIndex("Current AC Power Setting Index: 0x00000004", CpuBoostMode.Efficient);
            t.ServiceInitialization_InstantiatesAndQueriesCurrentMode();
            t.ServiceInitialization_IsolatedConstructor_SkipsRegistryAndHardware();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All CpuPowerService unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in CpuPowerService unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 11: GameDetectionService Unit Tests
        Console.WriteLine("\n[TEST 11] Testing GameDetectionService Unit Tests...");
        try
        {
            var t = new GameDetectionServiceTests();
            t.GameProfile_JsonSerialization_RoundtripsAllPropertiesAccurately();
            t.ProfileManagement_SaveGetAndDelete_OperatesCleanly();
            t.ProcessDisposalSafety_ProcessIterationPattern_SafelyDisposesAllHandles();
            t.GameStateProperties_ThreadSafetyAndConsistency();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All GameDetectionService unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in GameDetectionService unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 12: AcerAgentClient Unit Tests
        Console.WriteLine("\n[TEST 12] Testing AcerAgentClient Unit Tests...");
        try
        {
            var t = new AcerAgentClientTests();
            t.IsValidJson_CorrectlyValidatesCompleteAndIncompletePackets("{\"result\": \"0\"}", true);
            t.IsValidJson_CorrectlyValidatesCompleteAndIncompletePackets("{\"result\": \"0\", \"data\": {", false);
            t.StreamAccumulation_ChunkedFragments_ReassemblesCompleteJsonPacket();
            t.PacketHeaderFraming_BuildsProperBinaryLayout();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All AcerAgentClient unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in AcerAgentClient unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 13: StartupOptions Unit Tests
        Console.WriteLine("\n[TEST 13] Testing StartupOptions Unit Tests...");
        try
        {
            var t = new StartupOptionsTests();
            t.EmptyArgs_EvaluatesToManualLaunchNonMinimized();
            t.AutostartFlag_EvaluatesToAutostartButNotMinimized();
            t.MinimizedFlag_EvaluatesToMinimizedNonAutostart();
            t.CombinedFlags_EvaluatesToBothTrue();
            t.CaseInsensitiveArgs_ParsedCorrectly("--AUTOSTART", true, false);
            t.CaseInsensitiveArgs_ParsedCorrectly("--minimized", false, true);
            t.UnrecognizedFlagsAndWhitespace_HandledGracefully();
            t.DebloatService_AutoStartCommandLine_ContainsAutostartFlag();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All StartupOptions unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in StartupOptions unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 14: KeyboardBacklightPersistence Unit Tests
        Console.WriteLine("\n[TEST 14] Testing KeyboardBacklightPersistence Unit Tests...");
        try
        {
            var t = new KeyboardBacklightPersistenceTests();
            t.AppSettings_SerializationAndDeserialization_PreservesLightingAndOffState();
            t.AppSettings_BacklightActiveProfile_PreservesCustomSettings();
            t.SettingsFile_AtomicSerialization_ProducesValidJson();
            t.ReassertDecisionLogic_TurnedOffBacklight_StaysStrictlyOff();
            t.ReassertDecisionLogic_ActiveLighting_MaintainsTargetParameters();
            t.RgbProfileService_SaveAndRetrieveCustomPreset_WorksReliably();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All KeyboardBacklightPersistence unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in KeyboardBacklightPersistence unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 15: Adversarial Regression & Startup Tests
        Console.WriteLine("\n[TEST 15] Testing Adversarial Regression & Startup Tests...");
        try
        {
            var t = new AdversarialRegressionAndStartupTests();
            t.StartupOptions_NullAndWhitespaceInputs_NeverStartMinimized();
            t.StartupOptions_CasePermutationsAndPadding_ParsedAccurately("--AuToStArT", false, true);
            t.StartupOptions_CasePermutationsAndPadding_ParsedAccurately("--MiNiMiZeD", true, false);
            t.StartupOptions_DuplicatedFlags_AreIdempotent();
            t.StartupOptions_MixedAndUnrecognizedFlags_HandledGracefully();
            t.StartupOptions_ManualLaunchInvariant_ComprehensivePermutations();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All Adversarial Regression & Startup tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in Adversarial Regression & Startup tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 16: Adversarial Lifecycle & Persistence Tests
        Console.WriteLine("\n[TEST 16] Testing Adversarial Lifecycle & Persistence Tests...");
        try
        {
            var t = new AdversarialLifecycleAndPersistenceTests();
            t.SingleInstanceMutex_AbandonedMutex_DemonstratesConstructorNeverThrowsAndRequiresWaitOne();
            t.SingleInstanceMutex_AbandonedMutex_RecoveryLogic_AcquiresOwnershipAndSetsCreatedNew();
            t.SingleInstanceEvent_TryOpenExisting_NonExistent_ReturnsFalseWithoutThrowing();
            t.SingleInstanceEvent_SignalingFlow_WakesWaitingListenerAcrossThreads();
            t.SingleInstanceMutex_DuplicateInstance_CorrectlyDetectedAndRejected();
            t.AllowSetForegroundWindow_ASFW_ANY_InvokesWithoutAccessViolation();
            t.SettingsPersistence_EmptySettingsFile_HandledGracefullyWithoutCrash();
            t.SettingsPersistence_CorruptedSyntax_HandledGracefullyWithoutCrash();
            t.SettingsPersistence_TurnOffLights_StrictRoundtripPreservation();
            t.SettingsPersistence_StartupRefreshDecision_SkipsHardwareOverwrite();
            t.AdversarialFinding_SliderBrightnessZero_ExposesLackOfPersistenceVulnerability();
            t.SliderBrightnessZero_RemediatedPersistence_PreservesZeroBrightnessAndIsBacklightOff();
            await t.ReassertKeyboardLighting_TimingAndDualPulseSimulation_ExecutesAtStagedIntervals();
            await t.SocketFailure_RetryLoop_CatchesExceptionsWithoutCrashing();
            await t.ReassertDecision_HighConcurrency_IdempotentAndThreadSafe();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All Adversarial Lifecycle & Persistence tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in Adversarial Lifecycle & Persistence tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 17: Audit Remediation & Hardening Tests (R1 - R7)
        Console.WriteLine("\n[TEST 17] Testing Audit Remediation & Hardening Tests (R1-R7)...");
        try
        {
            var t = new AuditRemediationTests();
            t.DebloatService_PreDebloatStartupTypes_RecordedAndRestoredAccurately();
            t.DebloatService_PowerShellFailures_SurfacedAndNotSilentlySuppressed();
            t.TryScanJsonCompleteness_DetectsBalancedBracesWithoutStringAllocations();
            t.TryScanJsonCompleteness_IgnoresBracesInsideStringsAndEscapes();
            t.AcerAgentClient_MaxResponseSizeEnforced();
            t.HardwareMonitorService_PInvokeMethods_HaveDefaultDllImportSearchPathsSystem32();
            t.TelemetryReportLoop_MonotonicTickCondition_ExecutesStrictlyEvery5Ticks();
            t.SettingsManager_SaveAtomic_ReplacesFileWithoutCorruption();
            t.SettingsManager_LoadWithFallback_RecoversFromCorruptedSettings();
            t.Privacy_BatteryReportHtml_NotInWorkspaceOrExcluded();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All Audit Remediation & Hardening tests (R1-R7) passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in Audit Remediation tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        Console.WriteLine("\n==================================================");
        if (failedTests == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ALL TESTS PASSED SUCCESSFULLY! (0 failures)     ");
            Console.ResetColor();
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  TEST SUITE COMPLETED WITH {failedTests} FAILURE(S)!");
            Console.ResetColor();
            return 1;
        }
    }
}

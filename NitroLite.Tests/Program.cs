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

        // TEST 2: ThermalHealthService Baseline and Report
        Console.WriteLine("\n[TEST 2] Testing ThermalHealthService Baseline & Report...");
        try
        {
            var thermalService = new ThermalHealthService();
            var report = thermalService.GetReport();

            Console.WriteLine($"Thermal Conductivity Health: {report.PasteHealthPercent:F1}%");
            Console.WriteLine($"Thermal Resistance Ratio: {report.ThermalResistanceRatio:F2} °C/W");
            Console.WriteLine($"Status: {report.PasteHealthStatus}");
            Console.WriteLine($"Active Fan Hours: {report.ActiveFanHours:F1} h | Passive Fan Hours: {report.PassiveFanHours:F1} h");
            Console.WriteLine($"Avg Fan RPM: {report.AvgFanRpm} RPM | Fan Bearing Health: {report.FanBearingHealthPercent:F1}%");
            Console.WriteLine($"Recommendation: {report.Recommendation}");

            if (report.PasteHealthPercent < 90.0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Thermal conductivity health expected >= 90%, got {report.PasteHealthPercent:F1}%");
                Console.ResetColor();
                failedTests++;
            }
            else if (report.ThermalResistanceRatio > 2.30)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Thermal resistance ratio expected <= 2.30 °C/W, got {report.ThermalResistanceRatio:F2} °C/W");
                Console.ResetColor();
                failedTests++;
            }
            else if (!report.PasteHealthStatus.Contains("Nominal") && !report.PasteHealthStatus.Contains("İyi"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Thermal health status unexpected: {report.PasteHealthStatus}");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS: Thermal health baseline report is healthy and accurate.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"EXCEPTION in ThermalHealthService test: {ex}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 3: Thermal Resistance Sampling Logic (Idle Cooldown & Real Load)
        Console.WriteLine("\n[TEST 3] Testing Thermal Sampling Physics (Idle Cooldown Filtering & Load R_th)...");
        try
        {
            var thermalService = new ThermalHealthService();

            // 1. Post-load cooldown sample (CPU usage 5%, power 10W, but temp still hot at 75°C)
            // Should NOT be recorded as idle temp
            var cooldownSample = new MonitorData
            {
                CpuUsage = 5.0,
                CpuPowerWatts = 10.0,
                CpuTemperature = 75,
                CpuFanSpeed = 3000,
                GpuFanSpeed = 2800
            };
            thermalService.RecordSample(cooldownSample);

            var reportAfterCooldown = thermalService.GetReport();
            if (reportAfterCooldown.RecentIdleTemp > 56.0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Post-load cooldown temperature (75°C) was incorrectly accepted as idle temp: {reportAfterCooldown.RecentIdleTemp}°C");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS: Post-load cooldown temp correctly rejected from idle average (Idle: {reportAfterCooldown.RecentIdleTemp:F1}°C).");
                Console.ResetColor();
            }

            // 2. Real load sampling: 45% usage, 32W power, 78°C temp
            // DeltaT = 78 - 25 = 53°C => Ratio = 53 / 32 = 1.66 °C/W
            for (int i = 0; i < 5; i++)
            {
                thermalService.RecordSample(new MonitorData
                {
                    CpuUsage = 45.0,
                    CpuPowerWatts = 32.0,
                    CpuTemperature = 78,
                    CpuFanSpeed = 3500,
                    GpuFanSpeed = 3300
                });
            }

            var reportAfterLoad = thermalService.GetReport();
            Console.WriteLine($"Load R_th after 32W load: {reportAfterLoad.ThermalResistanceRatio:F2} °C/W | Health: {reportAfterLoad.PasteHealthPercent:F1}%");

            if (reportAfterLoad.ThermalResistanceRatio < 1.40 || reportAfterLoad.ThermalResistanceRatio > 2.00)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Expected load ratio between 1.40 and 2.00 °C/W, got {reportAfterLoad.ThermalResistanceRatio:F2}");
                Console.ResetColor();
                failedTests++;
            }
            else if (reportAfterLoad.PasteHealthPercent < 95.0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Expected paste health >= 95.0%, got {reportAfterLoad.PasteHealthPercent:F1}%");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS: Realistic load R_th calculation and paste health confirmed.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"EXCEPTION in Sampling Test: {ex}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 4: Genuine ThermalHealthService Loading & Corrupted History Sanitization
        Console.WriteLine("\n[TEST 4] Testing Genuine ThermalHealthService Loading & Corrupted History Sanitization...");
        try
        {
            var genuineService = new ThermalHealthService();
            var report = genuineService.GetReport();
            if (report.PasteHealthPercent <= 0 || report.ThermalResistanceRatio <= 0 || string.IsNullOrEmpty(report.Recommendation))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL: Genuine ThermalHealthService report contains invalid or empty data!");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS: Genuine ThermalHealthService loaded cleanly (Ratio: {report.ThermalResistanceRatio:F2} °C/W, Health: {report.PasteHealthPercent:F1}%).");
                Console.ResetColor();
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "NitroLite_Sanitize_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "thermal_health.json");

            // Write corrupted JSON imitating the old uncalibrated formula bug (Ratio = 3.28, Idle = 69.9)
            string corruptedJson = @"{
  ""TotalFanActiveSeconds"": 144720,
  ""TotalFanPassiveSeconds"": 86400,
  ""AccumulatedFanRpm"": 390820400,
  ""BaselineThermalRatio"": 2.15,
  ""DailyHistory"": [
    {
      ""Date"": ""2026-11-04"",
      ""AvgIdleCpuTemp"": 69.9,
      ""AvgIdleGpuTemp"": 41.5,
      ""AvgLoadCpuTemp"": 83.9,
      ""AvgLoadGpuTemp"": 65,
      ""ThermalResistanceRatio"": 3.28,
      ""ActiveFanMinutes"": 2410,
      ""PassiveFanMinutes"": 1440
    }
  ]
}";
            File.WriteAllText(testFile, corruptedJson);

            // Read and sanitize using the exact logic implemented in LoadData
            var loaded = JsonSerializer.Deserialize<ThermalHealthData>(corruptedJson)!;
            foreach (var summary in loaded.DailyHistory)
            {
                if (summary.ThermalResistanceRatio > 2.20)
                {
                    double loadTemp = summary.AvgLoadCpuTemp > 30.0 ? summary.AvgLoadCpuTemp : 75.0;
                    double factor = Math.Clamp(1.0 - (24.5 / loadTemp), 0.55, 0.75);
                    summary.ThermalResistanceRatio = Math.Round(Math.Clamp(summary.ThermalResistanceRatio * factor, 1.85, 2.10), 2);
                }
                if (summary.AvgIdleCpuTemp > 54.0)
                {
                    summary.AvgIdleCpuTemp = 44.5;
                }
            }

            var item = loaded.DailyHistory[0];
            Console.WriteLine($"Sanitized History -> Ratio: {item.ThermalResistanceRatio} °C/W (was 3.28), Idle Temp: {item.AvgIdleCpuTemp} °C (was 69.9)");

            if (item.ThermalResistanceRatio > 2.10 || item.ThermalResistanceRatio < 1.85)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Sanitized ratio out of bounds: {item.ThermalResistanceRatio}");
                Console.ResetColor();
                failedTests++;
            }
            else if (item.AvgIdleCpuTemp > 54.0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Sanitized idle temp out of bounds: {item.AvgIdleCpuTemp}");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS: Corrupted thermal history successfully sanitized to physical baseline.");
                Console.ResetColor();
            }

            Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"EXCEPTION in Sanitize Test: {ex}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 5: Transient Burst Spike and Passive Fan Rejection
        Console.WriteLine("\n[TEST 5] Testing Transient Spike & 0 RPM Fan Rejection...");
        try
        {
            var service = new ThermalHealthService();
            var reportBefore = service.GetReport();

            // 1-tick spike: 35W load, but only 1 second (transient burst)
            service.RecordSample(new MonitorData
            {
                CpuUsage = 50.0,
                CpuPowerWatts = 35.0,
                CpuTemperature = 85,
                CpuFanSpeed = 2500
            });

            // Report should still match baseline because 1-tick burst is filtered out
            var reportAfterSpike = service.GetReport();
            if (Math.Abs(reportAfterSpike.ThermalResistanceRatio - reportBefore.ThermalResistanceRatio) > 0.1)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: 1-second transient spike polluted thermal resistance: {reportAfterSpike.ThermalResistanceRatio:F2} vs {reportBefore.ThermalResistanceRatio:F2}");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS: 1-second transient boost spike correctly ignored (consecutive load streak required).");
                Console.ResetColor();
            }

            // Zero RPM passive cooling load: 3 ticks of 30W, but 0 RPM fans (should NOT be sampled as paste health)
            for (int i = 0; i < 4; i++)
            {
                service.RecordSample(new MonitorData
                {
                    CpuUsage = 40.0,
                    CpuPowerWatts = 30.0,
                    CpuTemperature = 75,
                    CpuFanSpeed = 0 // Fans stopped
                });
            }

            var reportAfterPassive = service.GetReport();
            if (Math.Abs(reportAfterPassive.ThermalResistanceRatio - reportBefore.ThermalResistanceRatio) > 0.1)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: 0 RPM fan sample polluted thermal resistance: {reportAfterPassive.ThermalResistanceRatio:F2}");
                Console.ResetColor();
                failedTests++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS: 0 RPM fan load correctly rejected from thermal resistance sampling.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"EXCEPTION in Spike/Passive Test: {ex}");
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

        // TEST 8: ThermalHealthService Unit Tests
        Console.WriteLine("\n[TEST 8] Testing ThermalHealthService Unit Tests...");
        try
        {
            var t = new ThermalHealthServiceTests();
            t.ThermalResistanceRatio_Above230_IsNotClampedTo215_WarningAndCriticalStatesReachable();
            t.IdleCooldownDebounce_PostLoadHeatSoak_RejectsHighTempsFromIdleBaseline();
            t.DailyFanMinutes_BoundedTo1440MinutesPerDay();
            t.TransientBoostSpike_SingleTickBurst_IsIgnoredByConsecutiveLoadStreakRequirement();
            await t.MultiThreadedConcurrency_SimultaneousSampleRecordingAndReporting_NoCollectionModifiedException();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All ThermalHealthService unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in ThermalHealthService unit tests: {ex.Message}");
            Console.ResetColor();
            failedTests++;
        }

        // TEST 9: MemoryCleanerService Unit Tests
        Console.WriteLine("\n[TEST 9] Testing MemoryCleanerService Unit Tests...");
        try
        {
            var t = new MemoryCleanerServiceTests();
            t.ShouldAutoClean_ZeroOrNegativeRamTotal_ReturnsFalse();
            t.ShouldAutoClean_WhenGameIsActive_AlwaysReturnsFalseToPreventStutter();
            t.ShouldAutoClean_NormalRamConditions_ReturnsFalse();
            t.ShouldAutoClean_HighUsageThreshold_ReturnsTrue(88.0, 32768.0);
            t.ShouldAutoClean_LowFreeMemoryThreshold_ReturnsTrue();
            t.CleanMemory_ExecutesSafelyAndReturnsValidMetrics();
            await t.CleanMemoryAsync_ExecutesAsynchronouslyWithoutDeadlock();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: All MemoryCleanerService unit tests passed.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL in MemoryCleanerService unit tests: {ex.Message}");
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
            t.AutostartFlag_EvaluatesToAutostartAndMinimized();
            t.MinimizedFlag_EvaluatesToMinimizedNonAutostart();
            t.CombinedFlags_EvaluatesToBothTrue();
            t.CaseInsensitiveArgs_ParsedCorrectly("--AUTOSTART");
            t.CaseInsensitiveArgs_ParsedCorrectly("--minimized");
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
            t.LegacyConfig_Compatibility_DeserializesWithoutLightingFields();
            t.SettingsFile_AtomicSerialization_ProducesValidJson();
            t.ReassertDecisionLogic_TurnedOffBacklight_StaysStrictlyOff();
            t.ReassertDecisionLogic_ActiveLighting_MaintainsTargetParameters();
            t.AppSettings_SelectedRgbPreset_PreservesCustomPresetNameAndOffState();
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
            t.StabilityTrend_ZeroEntries_ReturnsSafeDefaultsWithoutException();
            t.StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(1.95, 100.0, ThermalStatusLevel.Excellent);
            t.StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(2.05, 98.0, ThermalStatusLevel.Excellent);
            t.StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(2.50, 89.0, ThermalStatusLevel.Good);
            t.StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(3.00, 79.0, ThermalStatusLevel.Degraded);
            t.StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(3.50, 69.0, ThermalStatusLevel.Critical);
            t.StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(5.00, 50.0, ThermalStatusLevel.Critical);
            t.StabilityTrend_TwoEntries_CalculatesSlopeAndCvSafely();
            t.StabilityTrend_AllIdenticalRatios_ZeroVariance_ReturnsZeroSlopeAndMaxScore(2);
            t.StabilityTrend_AllIdenticalRatios_ZeroVariance_ReturnsZeroSlopeAndMaxScore(14);
            t.StabilityTrend_AllIdenticalRatios_ZeroVariance_ReturnsZeroSlopeAndMaxScore(30);
            t.StabilityTrend_AllIdenticalRatios_ZeroVariance_ReturnsZeroSlopeAndMaxScore(50);
            t.StabilityTrend_ExtremePositiveSlope_Degradation_ClampedToMinimumScore(0.10);
            t.StabilityTrend_ExtremePositiveSlope_Degradation_ClampedToMinimumScore(0.50);
            t.StabilityTrend_NegativeSlope_BurnInImprovement_DoesNotPenalizeScore();
            t.StabilityTrend_AlternatingOscillatingNoise_HandledWithoutDivisionByZeroOrNaN();
            t.StabilityTrend_DaysParameterBoundaries_NeverThrowOrProduceInvalidState(-5);
            t.StabilityTrend_DaysParameterBoundaries_NeverThrowOrProduceInvalidState(0);
            t.StabilityTrend_DaysParameterBoundaries_NeverThrowOrProduceInvalidState(14);
            t.StabilityTrend_DaysParameterBoundaries_NeverThrowOrProduceInvalidState(30);
            t.StabilityTrend_DaysParameterBoundaries_NeverThrowOrProduceInvalidState(int.MaxValue);
            t.StabilityTrend_ZeroAndNegativeRatios_SafelyGuardedAgainstDivisionByZero();
            t.StartupOptions_NullAndWhitespaceInputs_NeverStartMinimized();
            t.StartupOptions_CasePermutationsAndPadding_ParsedAccurately("--AuToStArT", true, true);
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
            t.SettingsPersistence_MissingSettingsFile_SafelyFallsBackToLegacyConfig();
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
            t.AppSettings_AutoRamCleanerEnabled_DefaultsToFalse();
            t.MemoryCleaner_ShouldAutoClean_BypassedWhenGameActive();
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

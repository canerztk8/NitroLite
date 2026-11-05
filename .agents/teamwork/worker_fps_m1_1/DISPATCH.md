# Task Assignment: Worker Milestone 1 (%0.1 Low FPS Calculation, Filtering & Clamping)

## Context
Implementing Milestone 1 of the NitroLite FPS telemetry stabilization project.

## Mission
Implement the complete, genuine algorithmic fix for %0.1 Low FPS calculation, microsecond filtering, and physical clamping:
1. Fix `NitroLite/Core/FpsMonitorService.cs`:
   - Eliminate raw reads from offset 5092 (which is inside `dwStatFrameTimeLowBuf[1024]` storing microsecond times, producing 398,798 FPS in Spotifast).
   - Implement dynamic live %0.1 Low FPS calculation from the 1024-entry frametime ring buffer (`dwStatFrameTimeBuf[1024]` starting at offset 924).
   - Implement strict microsecond noise filtering: Only accept frame times where $1,000\,\mu s \le FT \le 500,000\,\mu s$ ($1.0\,\text{ms} \le FT \le 500\,\text{ms}$). Discard microsecond glitches (<1ms) completely.
   - Enforce minimum sample threshold: If valid sample count $N < 10$, return $Low01Fps = 0.0$. When $N \ge 10$, calculate the standard 99.9th percentile ($p999Idx = \min(Count - 1, \lfloor(Count - 1) \times 0.999\rfloor)$ on sorted valid frame times) and compute $Low01Fps = 1,000,000.0 / p999Ft$.
   - Enforce physical upper-bound clamping: Under no circumstance can 0.1% Low FPS exceed average FPS ($Low_{0.1\%} \le AvgFPS$). If $Fps \le 0.0$, $Low01Fps = 0.0$. Also ensure $Low01Fps \le Low1Fps \le Fps$ if 1.0% Low is calculated.
2. Fix `NitroLite/Core/TelemetryFormatter.cs`:
   - Ensure `FormatLow01Fps` and `FormatFps` safely handle zero, negative, NaN, infinity, and unrealistically large values (>2000 FPS $\implies$ `"-- FPS"`).
3. Create `NitroLite.Tests/Low01FpsCalculationTests.cs`:
   - Implement all 12 test specifications documented in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_test_survey_1\survey_report.md` Section 5.1:
     - Clamping to average FPS (both extreme 398,798 and moderate 75 FPS on 60 FPS avg).
     - Zero average FPS yields zero Low01Fps.
     - Microsecond noise rejection (<1ms rejected, 1000us accepted).
     - Empty ring buffer, single sample, <10 samples handling.
     - Standard percentile calculation for uniform distribution and isolated stutter spikes.
     - Spotifast reproduction scenario proving 398798 FPS cannot occur.
     - Formatting boundaries.
4. Update `NitroLite.Tests/FpsTelemetryTests.cs` if any test previously relied on mock byte array writing 950 at offset 5092 to align with ring buffer calculations.
5. Verification:
   - Run `dotnet test NitroLite.sln` — all 320 baseline tests + all new tests must pass 100% green.
   - Run `dotnet build NitroLite.sln -c Release` — must build with 0 errors and 0 warnings.
6. Write full report in `.agents/teamwork/worker_fps_m1_1/handoff.md`.

## Mandatory Integrity Warning
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

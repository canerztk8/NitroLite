## 2026-11-04T02:00:39Z
You are worker_tests_1, a test automation and QA worker subagent for the NitroLite project.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_tests_1\
Your parent conversation ID is: e6d296af-2648-48c0-9375-0e4e563c211b

MANDATORY FIRST STEPS:
1. Read the user requirements file: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
2. Read the scope file: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\orchestrator_2\SCOPE.md
3. Read the upstream handoff reports:
   - c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_1\handoff.md
   - c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_ui_1\handoff.md
4. Initialize your BRIEFING.md and progress.md in your working directory.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

YOUR ASSIGNMENT: Milestone 2C — Test Suite Expansion & `dotnet test` Enablement
Files you own exclusively:
- `NitroLite.Tests/NitroLite.Tests.csproj`
- `NitroLite.Tests/Program.cs`
- Any new test source files in `NitroLite.Tests/`

ACCEPTANCE CRITERIA:
- The user acceptance criteria states:
  "All existing and newly added unit tests in NitroLite.Tests pass cleanly via dotnet test."
  "Test command: dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj or dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj"
- Both commands (`dotnet test` AND `dotnet run`) MUST run cleanly and pass 100% of tests with ZERO failures.

IMPLEMENTATION TASKS:
1. `NitroLite.Tests.csproj`:
   - Configure the test project so that `dotnet test` discovers and executes test classes (e.g. reference `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`).
   - If maintaining `Program.cs` for `dotnet run`, ensure the project builds and runs cleanly under both `dotnet test` and `dotnet run`.
2. Hardware Coupling Remediation in existing tests:
   - In Test 1 (StorageHealthService), do not hardcode a requirement that the machine must have `>= 2` drives; support single-drive machines (`drives.Count >= 1`).
   - In Test 4, ensure genuine `ThermalHealthService` loading and sanitization is verified.
3. Unit Test Expansion (create comprehensive unit tests in `NitroLite.Tests`):
   - `TelemetryFormatterTests`:
     - Test fan speed formatting (RPM and percentage relative to 6000 RPM, clamp to 100%, 0 RPM handling).
     - Test GPU sleep state handling (0°C -> "0 °C (Uyku / D3cold)", 0W -> "0.0 W (D3cold)").
     - Test CPU and GPU clocks (0MHz handling).
     - Test RAM / VRAM formatting (zero RAM fallback).
     - Test battery power flow strings (`IsDischarging`, `IsCharging`, `IsPluggedIn`, zero discharge rate while on battery).
   - `StorageHealthServiceTests`:
     - Multi-partition drive capacity calculations.
     - DeviceId regex parsing for non-numeric identifiers (e.g. "PHYSICALDRIVE0").
     - Health computation without hardcoded 99/98.
     - Single-drive vs multi-drive mapping.
   - `ThermalHealthServiceTests`:
     - Verification that thermal resistance ratios above 2.30 °C/W are NOT artificially clamped to 2.15 (warning and critical states are reachable).
     - Idle cooldown debounce verification (post-load heat soak rejection).
     - Multi-threaded concurrency simulation (verifying no `Collection was modified` exceptions).
     - Daily fan minutes bounded to 1440 min/day.
   - `MemoryCleanerServiceTests`:
     - `ShouldAutoClean` logic: returns false when a game is running or total RAM is zero.
     - Free memory thresholds.
   - `CpuPowerServiceTests`:
     - Boost mode enum mapping and PowrProf index parsing (Disabled=0, Enabled/Aggressive=2, Efficient=4).
   - `GameDetectionServiceTests`:
     - GameProfile serialization, process disposal safety, mode transition locks.
   - `AcerAgentClientTests`:
     - JSON packet framing and stream accumulation.
4. VERIFICATION:
   - Run `dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`.
   - Run `dotnet run --project c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`.
   - Both commands must succeed with exit code 0 and zero failures.
   - Write full details in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_tests_1\handoff.md`.
   - Notify orchestrator via `send_message`.

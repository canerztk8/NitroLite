# Dispatch for Remediation Worker: Complete Test Isolation in CpuPowerService

Working Directory: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_remediation\
Role: teamwork_worker

Inputs to read:
- ORIGINAL_REQUEST.md: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- Reviewer 2 Handoff & Feedback: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\reviewer_2\handoff.md
- Target files:
  - NitroLite/Core/CpuPowerService.cs
  - NitroLite.Tests/VerifiedDefectsRegressionTests.cs
  - NitroLite.Tests/SafeVerificationEndToEndTests.cs

Defect to Fix (Caught by Reviewer 2):
In `NitroLite/Core/CpuPowerService.cs`:
When `BypassNativePowerCalls = true`:
`SetBoostModeNative` was bypassed, but `SetBoostModeAsync` then fell directly through to `SetBoostModePowerCfg`, which executed `RunPowerCfg` -> spawning real `powercfg.exe /setactive SCHEME_CURRENT` against the host Windows operating system!
In `VerifiedDefectsRegressionTests.cs:1007` and `SafeVerificationEndToEndTests.cs:520`, this caused real `powercfg.exe` commands to run against the host OS during headless test runs, violating Requirement R4 ("Güvenlik kısıtı: Gerçek fare profili, güç planı, Windows servisi veya zamanlanmış görev ayarlarını değiştiren testleri bu doğrulamada çalıştırma...").

Required Solution:
1. In `NitroLite/Core/CpuPowerService.cs`:
   - When `BypassNativePowerCalls == true`:
     `SetBoostModeAsync` must NOT execute real `powercfg.exe` commands. Instead, it should complete the simulated write in-memory:
     ```csharp
     if (BypassNativePowerCalls)
     {
         UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
         IsModeVerified = true;
         return true;
     }
     ```
   - Also, if `PowerCfgRunner` or `ProcessInfoFactory` delegate is provided:
     Allow tests to provide mock `ProcessStartInfo` or mock execution runner for `RunPowerCfg` if specific powercfg failure modes are being tested.
   - In `QueryBoostModeExact(bool isAc)` or `QueryCurrentBoostModePowerCfg()`:
     When `BypassNativePowerCalls == true`, return simulated current mode without spawning real `powercfg.exe`.
2. In `NitroLite.Tests/VerifiedDefectsRegressionTests.cs` and `NitroLite.Tests/SafeVerificationEndToEndTests.cs`:
   - Verify that tests setting `BypassNativePowerCalls = true` complete without spawning real `powercfg.exe`.
   - Verify tests that specifically test `_hangingProcess` or `powercfg` exit codes (like `TestCase5a` and `TestCase5b`) continue using their mock `ProcessInfoFactory` or `ProcessMock` properly.
3. Verification:
   - Run `dotnet build -c Release NitroLite.sln` -> 0 errors, 0 warnings.
   - Run `dotnet test NitroLite.Tests/NitroLite.Tests.csproj` -> all tests pass cleanly.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Write your handoff report to:
C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_remediation\handoff.md
When finished, notify orchestrator via send_message.


## 2026-11-05T04:23:05Z
From: 1eb61985-d596-4a08-ac15-e4c49f4e70de (Orchestrator)
You are Remediation Worker (teamwork_worker).
Reviewer 2 requested changes on test safety:
When `BypassNativePowerCalls = true` in `CpuPowerService.cs`, it bypassed `powrprof.dll`, but fell through to `SetBoostModePowerCfg` which ran real `powercfg.exe` against the host OS in tests.

Fix `NitroLite/Core/CpuPowerService.cs` and verify tests in `NitroLite.Tests/VerifiedDefectsRegressionTests.cs` and `NitroLite.Tests/SafeVerificationEndToEndTests.cs`.
Ensure `BypassNativePowerCalls = true` prevents any real `powercfg.exe` process execution against the host OS.
Run `dotnet build -c Release NitroLite.sln` (0 errors, 0 warnings).
Run `dotnet test NitroLite.Tests/NitroLite.Tests.csproj` (all tests pass).

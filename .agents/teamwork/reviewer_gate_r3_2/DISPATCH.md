# Dispatch to reviewer_gate_r3_2

## Objective
Perform an independent, adversarial code review of Milestone 1 in NitroLite: Game OSD FPS/Frametime Integration (R3), UI/XAML styling, settings persistence, and regression prevention (R4).

## Working Directory
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\reviewer_gate_r3_2\`

## Authoritative User Request
Read verbatim from: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md`

## Reference Project Documentation
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\handoff.md`

## Review Focus & Scope
1. **Requirement R3 (Game OSD FPS & Frametime Integration)**:
   - Review `NitroLite/Core/FpsMonitorService.cs`, `OsdOverlayWindow.xaml`, `OsdOverlayWindow.xaml.cs`.
   - Verify RTSS shared memory bridge: memory-mapped file `"RTSSSharedMemoryV2"` access, `'RTSS'` signature `0x53535452`, version validation, `dwFrameTime` to ms conversion, and average FPS retrieval.
   - Verify graceful fallback when RTSS is not running or on desktop: no unhandled exceptions, no UI lag, clean `-- FPS` display.
   - Verify OSD HUD 4th row (`PnlOsdGame`): styling matches high-contrast dark monospace theme, zero layout shifting, correct color keys.
2. **UI & Settings Persistence**:
   - Review `NitroLite/MainWindow.xaml` and `MainWindow.xaml.cs`.
   - Verify Tab 4 Battery Card: `CmbDcPowerMode` and `ChkAutoRefreshRate` bindings, dual-unit discharge display, dynamic remaining time display.
   - Verify `settings.json` serialization and deserialization (`AppSettings.DcPowerMode`, `AppSettings.AutoRefreshRateSwitching`).
   - Verify all 136 existing `x:Name` UI elements and event handlers are preserved.
   - Verify zero anti-vibecoding patterns (no gradients, no blurred glass, no em-dashes).
3. **Requirement R4 & Build/Test Verification**:
   - Verify MUX switch code paths are completely untouched.
   - Execute `dotnet build NitroLite.sln` and `dotnet test NitroLite.sln`.
   - Verify all 249 tests pass.

## Deliverable & Verdict
Write your review report to:
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\reviewer_gate_r3_2\handoff.md`
Your handoff MUST state an explicit verdict: **APPROVE** or **REQUEST_CHANGES**.
When done, notify the orchestrator via `send_message`.


## 2026-11-04T14:39:24Z
You are reviewer_gate_r3_2.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\reviewer_gate_r3_2\
Read your task instructions in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\reviewer_gate_r3_2\DISPATCH.md
Read the authoritative user request in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
Read the project documentation: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md
Read the worker handoff report: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_nitrolite_1\handoff.md

Review Milestone 1 in NitroLite focusing on Requirements R3 and R4:
- FpsMonitorService & RTSSSharedMemoryV2 reader with graceful offline fallback.
- OsdOverlayWindow.xaml & .cs 4th row (PnlOsdGame) styling and layout.
- MainWindow.xaml & .cs UI controls (CmbDcPowerMode, ChkAutoRefreshRate, dual units).
- Zero anti-vibecoding patterns & stop-slop conformance.
- MUX switch protection invariant.
- Execute `dotnet build NitroLite.sln` and `dotnet test NitroLite.sln` (confirming all 249 tests pass).

Write your report to:
c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\reviewer_gate_r3_2\handoff.md
Provide an explicit verdict: APPROVE or REQUEST_CHANGES.
When done, notify me via send_message.

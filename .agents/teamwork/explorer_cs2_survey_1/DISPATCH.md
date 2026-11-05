# Task Assignment: Explorer CS2 Survey

## Context
Surveying telemetry capture mechanisms for Counter-Strike 2 (`cs2.exe`) and anti-cheat protected games in NitroLite.

## Mission
Investigate why RTSS fails to capture CS2 telemetry (Valve Trusted Mode) and evaluate solutions:
1. RTSS profile configuration for `cs2.exe` (injection delays, hooking levels, compatibility flags).
2. PresentMon / DXGI ETW fallback provider (Windows Event Tracing for DXGI/D3D/DWM/PresentMon events in .NET 10).
3. Hybrid telemetry architecture in `NitroLite` (seamless fallback when RTSS shared memory has no frame data for an active game).
Note: Administrator privilege requirement for ETW is acceptable per requirements.

## Scope & File Ownership
- Read-only exploration of NitroLite architecture, system RTSS profiles/configs, ETW / PresentMon APIs, and integration feasibility.
- Do NOT modify any source code files.
- Write your findings to `.agents/teamwork/explorer_cs2_survey_1/survey_report.md` and `.agents/teamwork/explorer_cs2_survey_1/handoff.md`.


## 2026-11-04T15:59:29Z
[Message] sender=e4b46c3d-cb49-4896-97ae-3dbd1b395c38 priority=MESSAGE_PRIORITY_HIGH
Task Objective:
1. Thoroughly investigate telemetry capture for Counter-Strike 2 (`cs2.exe`) and anti-cheat protected games:
   - Why does RTSS fail on CS2 (Valve Trusted Mode, blocklist/hook blocking, OSD status remaining `FPS: -- (RTSS Bekleniyor)`)?
   - Evaluate options:
     a) RTSS profile configuration for `cs2.exe` (can profile flags/injection delay/raster hook bypass or hook reliably without anti-cheat ban risk)?
     b) ETW (Event Tracing for Windows) / PresentMon fallback engine: DXGI / D3D / Dwm / Microsoft-Windows-DXGI ETW events in .NET 10. Does Microsoft-Windows-DXGI provide PresentStop or similar events? Can we consume ETW sessions in C#? Note: Admin privileges are acceptable for ETW/PresentMon fallback per user requirements.
     c) Hybrid architecture: How can `FpsMonitorService` or an `IFpsTelemetryProvider` smoothly fallback to ETW when a game like `cs2.exe` is detected or RTSS yields no frametime updates?
2. Detail the exact technical architecture, necessary P/Invoke / libraries / ETW providers, and implementation plan.
3. Write your complete analysis to `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_cs2_survey_1\survey_report.md` and your handoff to `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_cs2_survey_1\handoff.md`.
4. Send a completion message back to the orchestrator when finished. Do NOT write or modify any source code.

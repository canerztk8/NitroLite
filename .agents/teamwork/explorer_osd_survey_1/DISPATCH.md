# Dispatch to explorer_osd_survey_1

## Objective
Survey Requirement R3 (Game & OSD Frametime / FPS integration) and R4 (MUX switch protection & unit test preservation) in NitroLite.

## Working Directory
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\`

## Authoritative User Request
Read verbatim from: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md`

## Scope & Investigation Tasks
1. Investigate current OSD implementation (`OsdOverlayWindow.xaml`, `OsdOverlayWindow.xaml.cs`) and how telemetry metrics are routed and rendered.
2. Investigate game and fullscreen detection techniques (e.g., Win32 `GetForegroundWindow`, fullscreen window rect checks, process detection).
3. Investigate frametime and FPS monitoring architectures:
   - RivaTuner Statistics Server (RTSS) shared memory bridge (`RTSSSharedMemoryV2`).
   - Independent DirectX/DXGI presentation counters, ETW / PresentMon, or lightweight Windows gaming performance counters.
   - Compare stability, overhead, dependencies, and graceful fallback when no game or RTSS is running.
4. Investigate Requirement R4 strictly: Map all code paths that touch MUX switch / GPU display switching (e.g. DDS, `HardwareService.GpuMode`) and document strict exclusion zones.
5. Survey `NitroLite.Tests` (201 tests): Analyze test patterns, execution requirements (`dotnet test`), and formulate the unit test plan for all new features (R1, R2, R3).

## Deliverables
Write detailed findings to `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\analysis.md` and `handoff.md`.


## 2026-11-04T14:05:44Z
You are explorer_osd_survey_1.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\
Read your task description in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\DISPATCH.md
Read the authoritative user request in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md

Investigate Requirement R3 & R4:
1. Current OSD architecture in OsdOverlayWindow.xaml and OsdOverlayWindow.xaml.cs, how metrics are styled, updated, and laid out.
2. Game and fullscreen detection on Windows (GetForegroundWindow, fullscreen window rect calculation, 3D process checks).
3. In-game frametime and FPS monitoring options:
   - RTSS (RivaTuner Statistics Server) shared memory bridge (RTSSSharedMemoryV2).
   - Independent DirectX/DXGI presentation counters, ETW / PresentMon, or lightweight Windows performance counters.
   - Compare stability, dependency requirements, overhead, and graceful fallback when no game/RTSS is present.
4. Requirement R4 MUX switch protection: Identify all code files and methods touching MUX / GPU display switching (e.g. DDS, HardwareService.GpuMode) and document explicit DO-NOT-TOUCH boundaries.
5. NitroLite.Tests structure: Analyze the 201 existing unit tests, verification commands (dotnet build, dotnet test), and design unit test fixtures for the new features (R1, R2, R3).

Write your complete analysis to c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\analysis.md and your handoff to c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_osd_survey_1\handoff.md.
When finished, notify me using send_message with a summary and the paths to your report files.

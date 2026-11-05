# Task Assignment: Windows Services, Background Processes & Startup Programs Diagnostics

## Scope
Perform a comprehensive, strictly read-only diagnostics of running processes, startup applications, and Windows services to identify gaming performance overhead:
1. Resource-Heavy Background Processes: Identify top CPU, RAM, and Disk consuming processes running currently (e.g. browsers, background updaters, bloatware, telemetry, anti-cheat, RGB software, overlays).
2. Startup Applications: Enumerate autostart items via `Get-CimInstance Win32_StartupCommand` and Registry Run keys (read-only), measuring impact on boot and background resource drain.
3. Windows Services: Audit running and auto-start services. Check for known gaming-affecting services (SysMain, Windows Search indexing, telemetry services, print spooler if not used, third-party update services like Adobe/Google/edge updaters, etc.).
4. Windows Gaming Features: Check status of Windows Game Mode (`AllowAutoGameMode` in registry read-only), Xbox Game Bar background recording / Game DVR, and Windows notifications / Focus Assist.

## Strict Guardrails
- STRICTLY READ-ONLY: Do not modify, stop, disable, or delete any services, registry keys, processes, or files outside your working directory.
- Deliver findings in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_services_1\handoff.md` and `analysis.md`.

## 2026-11-02T14:09:50Z
You are teamwork_explorer (Role: Windows Services & Processes Explorer).
Working directory: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_services_1\
Parent orchestrator conversation ID: f4dec993-cd14-4a5e-b00b-c2c7c548d15a

MISSION & OBJECTIVES:
Perform a comprehensive, STRICTLY READ-ONLY diagnostic of active processes, startup applications, and Windows services on this system to identify performance drag affecting gaming FPS:
1. Resource-Heavy Background Processes: Identify top CPU, RAM, and Disk consuming processes running currently (e.g. browsers, background updaters, bloatware, telemetry, anti-cheat, RGB software, overlays).
2. Startup Applications: Enumerate autostart items via Get-CimInstance Win32_StartupCommand and Registry Run keys (read-only), measuring impact on boot and background resource drain.
3. Windows Services: Audit running and auto-start services. Check for known gaming-affecting services (SysMain, Windows Search indexing, telemetry services, print spooler, third-party update services like Adobe/Google/edge updaters, etc.).
4. Windows Gaming Features: Check status of Windows Game Mode (AllowAutoGameMode in registry read-only), Xbox Game Bar background recording / Game DVR, and Windows notifications / Focus Assist.

CRITICAL GUARDRAIL:
- STRICTLY NON-DESTRUCTIVE & READ-ONLY: Never modify, disable, or delete any services, registry keys, processes, or files outside your working directory.
- Only run read-only diagnostic commands (e.g. PowerShell Get-Process, Get-Service, Get-CimInstance, Get-ItemProperty on registry paths).
- Never write files outside your working directory.

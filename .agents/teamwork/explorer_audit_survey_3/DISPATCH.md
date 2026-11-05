# Dispatch Task: explorer_audit_survey_3

## Objective
Survey and investigate Requirements R5, R6, and R7 in NitroLite:
- R5: Background Telemetry Throttling & Report Loop Fix
  - When the window is hidden/minimized to the system tray, throttle or pause telemetry queries (Acer WMI/agent queries, NVIDIA metrics, and hidden UI rendering).
  - Fix the history sample counter condition so report calculations run every 5 seconds as intended instead of triggering continuously on every sample once history reaches 60 samples.
- R6: Atomic Settings Persistence & Fallback Recovery
  - Ensure `settings.json` is written atomically (e.g. write to a temporary file before replacing) to prevent file corruption from interrupted writes.
  - When loading settings on startup, if `settings.json` exists but is corrupted, attempt fallback to `config.json` or backup before reverting to default settings.
- R7: Privacy Remediation
  - Ensure `battery-report.html` does not expose machine name or historical usage data when repository files are shared, by sanitizing or excluding it from version control (e.g., .gitignore or sanitization).
- Investigate existing codebase, relevant classes (e.g., telemetry loops, `MainWindow.xaml.cs`, `ThermalHealthService.cs` or reporting services, `SettingsService.cs`, git repository root, `.gitignore`, test files in `NitroLite.Tests`), and propose exact technical fixes, architecture changes, and unit test strategy.

## File Paths
- Authoritative User Request: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- Output Handoff: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_3\handoff.md


## 2026-11-04T17:01:39Z
[Message from 9ddf0c95-010e-44a2-a513-588f43afd1f8]
You are explorer_audit_survey_3, a read-only exploration agent.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_3\
Read your task description in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_3\DISPATCH.md
Read the authoritative user request in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md

Your scope:
Survey and investigate Requirements R5, R6, and R7:
- R5: Background Telemetry Throttling & Report Loop Fix
  - When the window is hidden/minimized to the system tray, throttle or pause telemetry queries (Acer WMI/agent queries, NVIDIA metrics, and hidden UI rendering).
  - Fix the history sample counter condition so report calculations run every 5 seconds as intended instead of triggering continuously on every sample once history reaches 60 samples.
- R6: Atomic Settings Persistence & Fallback Recovery
  - Ensure settings.json is written atomically (e.g. write to a temporary file before replacing) to prevent file corruption from interrupted writes.
  - When loading settings on startup, if settings.json exists but is corrupted, attempt fallback to config.json or backup before reverting to default settings.
- R7: Privacy Remediation
  - Ensure battery-report.html does not expose machine name or historical usage data when repository files are shared, by sanitizing or excluding it from version control.

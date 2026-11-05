# Dispatch for Survey Explorer 3: Debloat Restore & Safe Testing Architecture

Focus:
- Analyze R3 & R4 requirements in ORIGINAL_REQUEST.md.
- Investigate Debloat service/feature in NitroLite: backup file format, service start type vs running/stopped state restore, error collection, scheduled tasks status verification, backup retention on partial failure, corrupt/legacy backup handling, PowerShell admin process timeout (20s) and lifecycle locking.
- Investigate `NitroLite.Tests` architecture, current test cases, test seams, and how tests can run safely without touching real hardware, mouse profile, powercfg, Windows services, or scheduled tasks.
- Specifically investigate tests that currently might invoke real services (like `CpuPowerService.SetBoostModeAsync`) and how to safely mock/fake them.
- Document exact files, methods, bugs, test seam designs, and fix recommendations.
- Output report to: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\analysis.md
- Deliver handoff to: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\handoff.md

## 2026-11-05T03:10:20Z
[Message] timestamp=2026-11-05T03:10:20Z sender=1eb61985-d596-4a08-ac15-e4c49f4e70de priority=MESSAGE_PRIORITY_HIGH
content=You are Survey Explorer 3 (teamwork_explorer).
Your working directory is: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\
The path to the original user request is: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
Your dispatch instructions are at: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\DISPATCH.md

You MUST read ORIGINAL_REQUEST.md before starting work.
Focus on Requirement R3 and R4:
- Debloat service/feature in NitroLite: backup file format, service start type vs running/stopped state restore, error collection, scheduled tasks status verification, backup retention on partial failure, corrupt/legacy backup handling, PowerShell admin process timeout (20s) and lifecycle locking.
- `NitroLite.Tests` architecture, current test cases, test seams, and how tests can run safely without touching real hardware, mouse profile, powercfg, Windows services, or scheduled tasks.
- Specifically investigate tests that currently might invoke real services (like `CpuPowerService.SetBoostModeAsync`) and how to safely mock/fake them.
- Map the code in NitroLite, pinpoint all affected files, methods, state variables, bugs, and fix recommendations.

Write your detailed findings to:
C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\analysis.md
Write your completion handoff to:
C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_3\handoff.md
Update progress.md in your directory as you work.
When finished, send a message to orchestrator with your summary.


# Dispatch Task: explorer_audit_survey_1

## Objective
Survey and investigate Requirements R1 and R2 in NitroLite:
- R1: Debloat Service Restoration & Error Propagation
  - Record each service's actual startup type before disabling it.
  - Restore services to their recorded previous startup types rather than unconditionally setting all to Automatic.
  - Do not suppress PowerShell errors during debloat operations, and surface failures to the UI/caller rather than reporting false success when changes fail.
- R2: Memory Cleaner Game Guard & Default State
  - Disable automatic RAM cleaning by default (`AutoCleanMemory = false`).
  - Suppress memory cleaning whenever a game is running—both when a game is initially detected and during periodic checks—preventing working set trimming and standby cache purging while games load or execute.
- Investigate existing codebase, relevant classes (e.g., `DebloatService.cs`, `MemoryCleanerService.cs`, UI controls, models/settings, test files in `NitroLite.Tests`), and propose exact technical fixes, architecture changes, and unit test strategy.

## File Paths
- Authoritative User Request: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- Output Handoff: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_1\handoff.md


## 2026-11-04T17:01:21Z
[Message] timestamp=2026-11-04T17:01:21Z sender=9ddf0c95-010e-44a2-a513-588f43afd1f8 priority=MESSAGE_PRIORITY_HIGH content=You are explorer_audit_survey_1, a read-only exploration agent.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_1\
Read your task description in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_1\DISPATCH.md
Read the authoritative user request in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md

Your scope:
Survey and investigate Requirements R1 and R2:
- R1: Debloat Service Restoration & Error Propagation
  - Record each service's actual startup type before disabling it, and restore services to their recorded previous startup types rather than unconditionally setting all services to Automatic.
  - Do not suppress PowerShell errors during debloat operations, and surface failures to the UI/caller rather than reporting false success when changes fail.
- R2: Memory Cleaner Game Guard & Default State
  - Disable automatic RAM cleaning by default (AutoCleanMemory = false).
  - Suppress memory cleaning whenever a game is running—both when a game is initially detected and during periodic checks—preventing working set trimming and standby cache purging while games load or execute.

Instructions:
1. Explore the existing codebase for debloat operations (DebloatService.cs or similar), memory cleaner operations (MemoryCleanerService.cs, AutoCleanMemory settings), game detection integration, and existing tests in NitroLite.Tests.
2. Inspect the current logic, file paths, line numbers, error handling, and test coverage.
3. Determine exact changes needed for R1 and R2, identifying any potential risks, side-effects, or interface updates.
4. Propose a concrete implementation plan, code diff guidance, and unit test strategy for NitroLite.Tests.
5. Write your complete, structured findings to: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_1\handoff.md
6. Update your progress.md and send a completion message with send_message to the orchestrator.

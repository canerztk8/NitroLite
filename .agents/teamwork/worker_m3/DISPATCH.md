# Dispatch for Worker 3: Milestone M3 (Game Profile & Desktop Restore Concurrency)

Working Directory: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m3\
Exclusive Write Ownership:
- NitroLite\MainWindow.xaml.cs
- NitroLite\Core\PowerAutomationService.cs
(DO NOT modify any other files!)

Inputs to read:
- ORIGINAL_REQUEST.md: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- PROJECT.md: C:\Users\caner\Documents\antigravity\eager-shannon\PROJECT.md
- Explorer 1 Analysis: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_1\analysis.md
- Explorer 1 Handoff: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_survey_1\handoff.md

Detailed Requirements (R1):
1. Desktop baseline reference:
   - Introduce `_savedFanModeBeforeGame` and track `_currentFanMode`.
   - When game ends or desktop is restored, restore to `_savedFanModeBeforeGame`, NOT unconditionally `FanMode.Auto`. If user had selected Max fan before game, preserve and restore Max fan.
   - Save desktop reference before the first game transition; if a previous restore is pending or was interrupted, a new game must not overwrite this original desktop reference.
2. Unattempted restore steps preservation:
   - Pre-mark steps as pending before executing sequential restore steps, so that if execution is interrupted before reaching a step (e.g. session generation increment), unattempted steps remain pending and are not lost.
3. Success-only UI updates:
   - Update UI status (`TxtGameStatus`, toasts, balloon tips) ONLY after hardware writes confirm success. Do not update optimistically or when writes return false.
4. Active game and battery monitoring flags:
   - When game closes, clear active game flags; preserve failed restore steps and original targets.
5. Safe lock transfer on new game:
   - When a new game starts while an old flow is executing a hardware write, safely complete the in-flight hardware command and hand over the transition lock without executing stale subsequent commands.
6. Mouse restore retention:
   - In `OnGameEnded`, do NOT reset `_pendingMouseRestore = false;` in the else branch. If a subsequent game has no mouse profile or fails its write, retain the pending mouse restore record.
7. Accurate failure reporting:
   - Do not display an overall success toast if any restore step failed; enable independent steps to be retried and clearly report which steps failed.
8. Tray menu item:
   - Add "Masaüstü ayarlarını yeniden dene" to `_notifyIcon.ContextMenuStrip`.
   - Enable this item ONLY when pending restore steps exist (`_hasPendingDesktopRestore`).
9. Session generation in retry:
   - In `RetryPendingDesktopRestoreAsync()`, acquire `_gameProfileTransitionLock`, verify active game is not running, and check `_gameSessionGeneration` before and after each hardware step to prevent overwriting new games or issuing duplicate writes.
10. Synchronization with live save and power automation:
    - In `BtnSaveGame_Click` and `PowerAutomationService`, acquire `_gameProfileTransitionLock` and respect session generation so user requests and power source transitions do not clobber each other.
11. Test Seams:
    - Provide testable seams / internal constructor / transition handlers (`HandleGameStartedAsync`, `HandleGameEndedAsync`, `TestSeams`) so Milestone M4 tests can exercise complete game transition and restore flows headlessly.

12. Verification:
    - `dotnet build -c Release NitroLite/NitroLite.csproj` (0 errors, 0 warnings).
    - `dotnet test NitroLite.Tests/NitroLite.Tests.csproj` (all existing tests pass).

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Write your handoff report to:
C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m3\handoff.md


## 2026-11-05T03:36:09Z
[Message] timestamp=2026-11-05T03:36:09Z sender=1eb61985-d596-4a08-ac15-e4c49f4e70de priority=MESSAGE_PRIORITY_HIGH content=You are Worker 3 (teamwork_worker).
Your working directory is: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m3\
The path to the original user request is: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
Your dispatch file is at: C:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_m3\DISPATCH.md

You MUST read ORIGINAL_REQUEST.md before starting work.
Your task is Milestone M3: Game Profile & Desktop Restore Concurrency (Requirement R1).
Exclusive write files:
- NitroLite\MainWindow.xaml.cs
- NitroLite\Core\PowerAutomationService.cs
DO NOT modify any other files.

Implement all 11 requirements from DISPATCH.md and Explorer 1 analysis:
- Desktop baseline reference (track fan mode, do not force Auto, protect initial reference across interruptions).
- Pre-mark restore steps to preserve unattempted steps across interruptions.
- Success-only UI updates (no optimistic toasts/status).
- Active game flags cleanup on close while preserving pending restore targets.
- Safe lock transfer between games.
- Retain pending mouse restore across games without mouse profile.
- Accurately report restore failures and enable individual step retry.
- Bind "Masaüstü ayarlarını yeniden dene" to tray menu, enabled only when pending restore exists.
- In `RetryPendingDesktopRestoreAsync()`, acquire `_gameProfileTransitionLock` and check session generation at every step.
- Synchronize live profile save and power automation with `_gameProfileTransitionLock`.
- Add internal test seams on `MainWindow` for headless testing in M4.

Verify:
- `dotnet build -c Release NitroLite/NitroLite.csproj` (0 errors, 0 warnings).
- `dotnet test NitroLite.Tests/NitroLite.Tests.csproj` (all existing tests pass).

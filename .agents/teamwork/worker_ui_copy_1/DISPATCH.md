## 2026-11-04T11:45:17Z
You are worker_ui_copy_1, a teamwork_worker.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_ui_copy_1\
Your parent is orchestrator (conv id: 4b4def4c-162e-4600-a7bc-261d7de88c18).

Read ORIGINAL_REQUEST.md at: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
Read PROJECT.md at: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md

Read the authoritative survey reports:
1. UI & Design System Blueprint:
   c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_ui_survey_1\survey_ui_report.md
   c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_ui_survey_1\handoff.md
2. Copywriting & De-Slop Specification:
   c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_copy_survey_1\survey_copy_report.md
   c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_copy_survey_1\handoff.md
3. Hardware Logic & Zero-Regression Checklist:
   c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md
   c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_integrity_survey_1\handoff.md

Also review the domain skills:
- ui-ux-designer: C:\Users\caner\.gemini\config\skills\ui-ux-designer\SKILL.md
- stop-slop: C:\Users\caner\.gemini\config\skills\stop-slop\SKILL.md

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Your write ownership:
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml`
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs`
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml`
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml.cs`
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\Core\RgbProfileService.cs`
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\Core\ThermalHealthService.cs` (if updating de-slopped status strings)
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\Core\StorageHealthService.cs` (if updating de-slopped status strings)
- `c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\` (to synchronize any test assertions matching updated status strings)

Your tasks:
1. **R1: UI & Visual Anti-Vibecoding Overhaul**:
   - Refactor `MainWindow.xaml` and `OsdOverlayWindow.xaml` per the design system in `survey_ui_report.md`.
   - Remove purple-to-blue gradients, gradient hero text, colored border cards, blurred glassmorphism drop shadows (reduce to subtle 2-4px or crisp 1px borders), low-contrast muddy dark mode (adopt solid `#0F1115` base, `#13161D` sidebar/titlebar, `#171B24` cards, `#222733` interactive controls, `#252B38` subtle borders), emojis in headings, 3-icon boxes in a row, unstyled template cards, hover button fades, and inconsistent spacing (apply strict 4px/8px layout grid).
   - Standardize typography to Windows 11 `Segoe UI Variable Text` / `Segoe UI` with monospace tabular figures for numbers.
2. **R2: Copywriting & Text De-Slop**:
   - Audit and rewrite all user-facing text, section titles, telemetry labels, button text, tray menus, toast messages, and tooltips across `MainWindow.xaml`, `MainWindow.xaml.cs`, `OsdOverlayWindow.xaml`, `OsdOverlayWindow.xaml.cs`, and `RgbProfileService.cs` per `survey_copy_report.md`.
   - Purge all 111 emojis.
   - Eliminate all em-dashes (`—`, `--`, or spaced hyphens ` - ` used as em-dashes).
   - Eliminate generic AI buzzwords ("Akıllı", "Mükemmel", "Harika", "Stüdyo", "Proaktif Bakım Asistanı", "RAM Yiyor!", "Zengin", "Tertemiz ve ferah", "Tebrikler!"). Replace with clean, direct, human-written technical labels.
3. **R3: Strict Zero-Regression Hardware & Logic Preservation**:
   - Preserve all 136 code-behind `x:Name` fields in `MainWindow.xaml`.
   - Preserve all 11 `x:Name` controls in `OsdOverlayWindow.xaml`.
   - Preserve all 43 event handlers and binding wiring.
   - Preserve all 5 SolidColorBrush resources (`AccentGreen`, `AccentCyan`, `AccentRed`, `TextSecondary`, `TextMuted`).
   - Preserve all `Tag` properties for hardware mode, boost, chart metrics, and RGB colors.
   - Preserve uppercase prefix tokens in `CmbRgbEffect` (`STATIC`, `WAVE`, etc.) for Acer hardware socket protocol.
   - Preserve OSD Win32 click-through styles and double-click corner cycling.
   - If any status strings in `ThermalHealthService` or `StorageHealthService` are updated, update the unit tests in `NitroLite.Tests` in exact lockstep so all tests pass cleanly.
4. **Verification**:
   - Run `dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln`
   - Run `dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`
   - Verify 0 build errors, 0 build warnings, and 100% test pass rate (all 201+ tests passing).

## 2026-11-04T11:55:57Z
**Context**: Milestone 1 Implementation (UI anti-vibecoding & copy de-slop)
**Content**: Checking in on implementation status. Have you begun modifying MainWindow.xaml and the code-behind files? Please update your progress.md with current status.
**Action**: Report your current implementation step and any blockers or ETA.

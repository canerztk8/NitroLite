# BRIEFING — 2026-11-04T17:11:00Z

## Mission
Investigate 0.1% Low FPS and frametime tracking logic in NitroLite, explain the root cause of absurd values (e.g. 398798 FPS), analyze mathematical/architectural requirements, and prepare survey report and handoff.

## 🔒 My Identity
- Archetype: teamwork_explorer
- Roles: Low FPS Survey Explorer
- Working directory: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_fps_survey_1
- Original parent: e4b46c3d-cb49-4896-97ae-3dbd1b395c38
- Milestone: Investigation and Survey of %0.1 Low FPS & Frametime Logic

## 🔒 Key Constraints
- Read-only investigation — do NOT implement or modify any source code files
- Only write within working directory .agents/teamwork/explorer_fps_survey_1/
- Produce survey_report.md and handoff.md
- Send message back to orchestrator upon completion

## Current Parent
- Conversation ID: e4b46c3d-cb49-4896-97ae-3dbd1b395c38
- Updated: 2026-11-04T15:59:23Z

## Investigation State
- **Explored paths**:
  - `NitroLite/Core/FpsMonitorService.cs`
  - `NitroLite/Core/TelemetryFormatter.cs`
  - `NitroLite/Core/Models.cs`
  - `NitroLite/OsdOverlayWindow.xaml.cs`
  - `NitroLite/MainWindow.xaml.cs`
  - `NitroLite.Tests/FpsTelemetryTests.cs`
  - `NitroLite.Tests/AdversarialRtssChallengeTests.cs`
  - `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h`
  - `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\PMDPSharedMemory.h`
  - `C:\Program Files (x86)\RivaTuner Statistics Server\Profiles\Global`
- **Key findings**:
  - Identified exact root cause of 398798 FPS in Spotifast: `dwStatFrameTimeLowBuf[1024]` (4096 bytes) was overlooked in `RTSS_SHARED_MEMORY_APP_ENTRY`, causing offset 5092 to read the 4th element of the low frametime buffer ($3,987,980\,\mu s$ for Spotifast idle tick). Dividing by 10 produced 398798 FPS.
  - Absence of clamping: $FPS_{0.1\% Low} \le FPS_{avg}$ was not enforced anywhere.
  - Microsecond noise filtering was insufficient (`ft > 500` allowed 500 us / 2000 FPS).
  - CS2 Valve Trusted Mode blocks `RTSSHooks64.dll`, leaving RTSS shared memory empty for `cs2.exe`. Formulated non-injected fallback architecture using Windows DXGI ETW provider `{CA11C00C-18C3-436A-B4C3-BBE54F002EC3}` / PresentMon.
- **Unexplored areas**: None (task complete).

## Key Decisions Made
- Confirmed that reading raw offset 5088/5092 should be removed; dynamic calculation from the 1024-entry frametime ring buffer (`dwStatFrameTimeBuf` at offset 924) should be the primary live 0.1% Low mechanism.
- Recommended strict invariant clamping: $0 \le \text{Low01Fps} \le \text{Low1Fps} \le \text{Fps}$.
- Recommended microsecond noise filter: $1,000\,\mu s \le FT \le 500,000\,\mu s$.

## Artifact Index
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_fps_survey_1\survey_report.md` — Detailed root cause, mathematical formulation, and architecture survey.
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_fps_survey_1\handoff.md` — 5-component self-contained handoff report.

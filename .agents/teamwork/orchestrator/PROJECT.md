# Project: Windows Gaming FPS Performance Diagnostics & Optimization

## Architecture
- Diagnostic Track:
  - Stream A: Hardware, CPU, Memory (RAM frequency/XMP/channels), Storage IOPS/health, Thermals, Throttling, Power Profile.
  - Stream B: Windows Services, Background Processes (CPU/RAM hogs), Autostart entries, Windows Game Mode / Xbox Game Bar.
  - Stream C: GPU specifications, VRAM, Driver version & recency, Display resolution & refresh rate, HAGS, VRR/G-Sync, Graphics performance settings.
- Deliverable Track:
  - Comprehensive, professional markdown report at `fps_analysis_report.md` in the project root.
- Verification Track:
  - Quality and safety review (Reviewers + Challengers).
  - Non-destructive integrity audit (Auditor).

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Hardware & Thermal Diagnostics | CPU specs, core count, thermal status, RAM speed/channels/capacity, Storage type/space, Power plan | M1 | survey |
| 2 | Services & Process Diagnostics | Top background CPU/RAM processes, startup programs, non-essential services | M2 | survey |
| 3 | GPU & Display Diagnostics | GPU model, driver date/version, monitor refresh rate, resolution, HAGS, VRR | M3 | survey |
| 4 | Prioritized FPS Optimization Report | Generation of fps_analysis_report.md with High/Medium/Low priority, rationale, manual steps, risk assessment | M4 | synthesis |
| 5 | Review & Non-Destructive Integrity Audit | Empirical validation of recommendations, safety check, non-destructive compliance verification | M5 | verification |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | M1: Hardware & Thermals | Deep read-only analysis of CPU, thermals, throttling, RAM, storage, power plan | none | IN_PROGRESS |
| 2 | M2: Services & Processes | Deep read-only analysis of processes, autostart programs, gaming-affecting services | none | IN_PROGRESS |
| 3 | M3: GPU & Display | Deep read-only analysis of GPU, VRAM, drivers, refresh rates, display settings | none | IN_PROGRESS |
| 4 | M4: Report Synthesis | Generate comprehensive fps_analysis_report.md in workspace root | M1, M2, M3 | PLANNED |
| 5 | M5: Review & Audit | Independent peer review, adversarial testing, and forensic audit | M4 | PLANNED |

## Interface Contracts
- Explorers provide structured evidence in their respective `handoff.md` files:
  - Explicit hardware specs, measured metrics, command outputs.
  - Identified bottlenecks and issues.
  - Concrete suggestions with technical rationale.
- Worker consumes Explorers' `handoff.md` and generates root `fps_analysis_report.md`.
- Reviewers and Auditor verify `fps_analysis_report.md` against original system data and safety constraints.

## Code Layout
- Target Deliverable: `c:\Users\caner\Documents\antigravity\eager-shannon\fps_analysis_report.md` (written strictly by Worker)
- Agent Metadata: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\`
- No other files are created or modified. All system operations are STRICTLY READ-ONLY.

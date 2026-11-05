# Execution Plan: Read-Only Gaming FPS Optimization Analysis

## Objective
Analyze the host Windows system across hardware, thermals/throttling, background processes, startup programs, Windows services, memory, and GPU/drivers to produce a prioritized, actionable, read-only diagnostic report (`fps_analysis_report.md`) for general Windows gaming FPS improvement without modifying any system settings.

## Phases

### Phase 1: Exploration & Diagnostics (3 Parallel Streams)
- **Explorer 1 (Hardware & Thermals)**:
  - Working dir: `.agents/teamwork/explorer_hardware_1/`
  - Scope: CPU, RAM specifications (clock speed, XMP/DOCP status, channels), Storage (SSD/NVMe health, free space, trim), motherboard/BIOS, thermal status, throttling indicators, power profile settings (`powercfg`).
- **Explorer 2 (Windows Services & Background Processes)**:
  - Working dir: `.agents/teamwork/explorer_services_1/`
  - Scope: High CPU/RAM/Disk consuming background processes, Startup apps (`Get-CimInstance Win32_StartupCommand`, Registry Run keys read-only), non-essential Windows services (SysMain, telemetry, Windows Search, Xbox services, etc.), Game Mode / Game Bar status.
- **Explorer 3 (GPU, Drivers & Display Configuration)**:
  - Working dir: `.agents/teamwork/explorer_gpu_1/`
  - Scope: Dedicated vs integrated GPU, GPU driver version & release date, display resolution and refresh rate (Hz), G-Sync/FreeSync status, Hardware-Accelerated GPU Scheduling (HAGS), Variable Refresh Rate (VRR), graphics power plan.

### Phase 2: Synthesis & Deliverable Generation
- Synthesize all findings from Explorers 1, 2, and 3.
- Dispatch **teamwork_worker** to generate `fps_analysis_report.md` in the workspace root.
  - Structure: Hardware Specs, OS Build, Bottleneck Diagnostics, Prioritized Recommendations (High/Medium/Low Impact), Step-by-Step Manual Guide, Risk Assessments, Rollback procedures.

### Phase 3: Review & Challenge
- Dispatch **teamwork_reviewer_1** and **teamwork_reviewer_2** to verify completeness, accuracy, readability, safety, and ensure all recommendations have detailed manual instructions.
- Dispatch **teamwork_challenger_1** and **teamwork_challenger_2** to test and verify recommendation validity, ensure no misleading or snake-oil advice, and verify zero system alterations.

### Phase 4: Forensic Integrity Audit
- Dispatch **teamwork_auditor** to perform integrity audit, ensuring:
  - Non-destructive compliance (no files, services, registry modified)
  - Authentic hardware and driver diagnostic data (no fabricated or placeholder metrics)
  - Clean audit verdict.

### Phase 5: Gating & Delivery
- Validate gate criteria in `GATE_STATUS.md`.
- Deliver final summary to user/sentinel.

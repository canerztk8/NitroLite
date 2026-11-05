# Plan: MUX Switch Setting Root Cause Analysis

## Objective
Analyze why the MUX switch option in NitroSense disappeared after previous project modifications, identify the precise technical root cause with empirical command outputs, and generate a comprehensive forensic report (`mux_analysis_report.md`).

## Phase 0: Forensic Survey & Root Cause Investigation
- **Explorer 1 (NitroSense Internals)**:
  - Inspect NitroSense UI assets (Electron/ASAR packages, JavaScript bundles, React/Vue components) for the MUX switch toggle/visibility conditions (feature flags, WMI queries, registry keys, service checks).
  - Inspect Acer system services (Acer System Event Service, Acer Agent, ADES, PredSenseService, etc.) and WMI methods (e.g. AcerHardwareInterface, AcerSysInfo, etc.).
- **Explorer 2 (Display Subsystem & Hardware State)**:
  - Inspect current display adapter states (Intel UHD/Iris Xe + NVIDIA GeForce RTX).
  - Query NVIDIA DDS / Advanced Optimus status, display connection topology, driver version, display modes, and NVAPI/WMI states.
  - Check relevant registry keys (e.g., `HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm`, Acer-specific registry keys, D3DKMT, display mode configs).
- **Explorer 3 (Historical Git & Workspace Delta Investigation)**:
  - Analyze git commits, staged/unstaged changes, and existing scripts (`check_nvidia_dds.ps1`, `Optimize-PC-Admin.ps1`, `inspect_ades.ps1`, previous reports).
  - Find out what exact commands, scripts, or registry tweaks were executed in previous sessions that modified MUX, DDS, display switching, or services.

## Phase 1: Milestone Decomposition & Report Drafting
- Synthesize findings from Explorers into `PROJECT.md`.
- Dispatch Worker to write the comprehensive `mux_analysis_report.md` fulfilling all requirements (R1, R2, Acceptance Criteria).
- Ensure report includes:
  1. Executive Summary & Root Cause Statement.
  2. NitroSense MUX Visibility Logic (code snippets from JS/WMI/Service).
  3. Historical Delta: What script/change triggered the disappearance.
  4. Current Hardware/Software State & Empirical Evidence (actual PowerShell / WMI / registry query outputs).
  5. Contradiction Analysis: Why the current state causes NitroSense to hide the MUX switch.
  6. Actionable Remediation Steps.

## Phase 2: Multi-Agent Review, Empirical Challenge & Forensic Audit Gate
- 2 Reviewers independently review the report against R1, R2, Acceptance Criteria, and technical rigor.
- 2 Challengers test and challenge the claims empirically by running verification checks on the commands and outputs.
- 1 Forensic Auditor (`teamwork_auditor`) audits the evidence for zero cheating, no fabrication, and authentic command execution outputs.
- Orchestrator checks the Gate in `GATE_STATUS.md`. If passed, conclude and report to caller.

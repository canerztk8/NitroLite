# DISPATCH: explorer_mux_ui_1
Target: Investigate NitroSense application internals, JavaScript/Electron bundles, Acer services, and WMI/API calls that control the visibility and availability of the MUX switch.

## 2026-11-04T14:51:28Z
Sender: e17b238e-3662-441a-bb3f-3255bc7e9c03
Message:
You are an Explorer subagent (identity: explorer_mux_ui_1).
Working directory: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_mux_ui_1\
Original request path: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md

MANDATORY FIRST STEP: Read c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md completely.
Also maintain your own BRIEFING.md and progress.md in your working directory.

Objective:
Investigate the NitroSense application frontend and backend architecture to discover the exact code logic, feature flags, service checks, or WMI queries that determine whether the MUX switch UI toggle is displayed or hidden.

Scope & Tasks:
1. Locate the NitroSense application files on the system:
   - Check `C:\Program Files\Acer`, `C:\Program Files (x86)\Acer`, `C:\ProgramData\Acer`, WindowsApps (`*NitroSense*`), and any existing scripts in the workspace that reference NitroSense files (e.g. `find_asar.ps1`, `inspect_nitrosense_js.ps1`, `list_unpacked.ps1`, etc.).
2. Inspect the unpacked Electron/ASAR assets or JavaScript bundles for:
   - References to "MUX", "Display Mode", "GPU Mode", "Discrete GPU", "Optimus", "DDS", "Advanced Optimus", "Switchable Graphics", "DiscreteOnly".
   - Find the exact UI component and conditional render checks (`v-if`, `ngIf`, `isVisible`, `supportsMux`, feature matrix, etc.).
3. Inspect Acer backend services and WMI classes:
   - Check Acer System Event Service (ADES), Predator Sense Service, Acer Hardware Interface (`AcerHardwareInterface.dll`, `AcerSysInfo`, etc.).
   - Find which method or WMI property reports MUX support or current mode to NitroSense.
4. Scope boundary:
   - Read-only exploration. DO NOT edit or delete any files or configurations outside your working directory.
5. Output Requirements:
   - Write a detailed, evidence-backed report to `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_mux_ui_1\handoff.md`.
   - Include exact file paths, code snippets, function names, and query outputs demonstrating how NitroSense decides to show or hide the MUX switch.
   - When finished, send a message to the orchestrator (conversation ID: e17b238e-3662-441a-bb3f-3255bc7e9c03) with a concise summary and path to your handoff.md.

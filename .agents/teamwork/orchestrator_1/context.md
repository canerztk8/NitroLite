# Context: NitroSense MUX Switch Root Cause Analysis

## Background
- User experienced that the MUX switch setting previously present in NitroSense disappeared after previous project activities that made changes to MUX / display settings.
- Machine is an Acer Nitro gaming laptop with hybrid GPU (Intel / NVIDIA RTX).
- Workspace contains artifacts from prior projects, including `check_nvidia_dds.ps1`, `inspect_ades.ps1`, `inspect_nitrosense_js.ps1`, `nitro_sense_analysis.md`, `fps_analysis_report.md`, `NitroLite`, and various PowerShell scripts.
- Goal: Find the exact technical reason why NitroSense is hiding or removing the MUX switch, provide empirical command outputs, and explain which specific prior modification caused it.

## Key References
- `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md`
- NitroSense application location (typically in `C:\Program Files\Acer\NitroSense V3.x` or `C:\Program Files\Acer\NitroSense Service` or WindowsApps package)
- System services: Acer System Event Service (ADES), Acer Software (PredSenseService, etc.)
- WMI namespaces: `root\wmi`, `root\cimv2`
- NVIDIA Display Driver Service / Advanced Optimus (DDS)

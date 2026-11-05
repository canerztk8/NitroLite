# Dispatch Task: explorer_audit_survey_2

## Objective
Survey and investigate Requirements R3 and R4 in NitroLite:
- R3: Acer Agent Client Framing & Robust Communication
  - Resolve packet fragmentation and timeout handling in `AcerAgentClient` so partial JSON responses are not prematurely returned or truncated after 25ms.
  - Prevent late-arriving bytes from contaminating subsequent command responses.
  - Close or drain connections on incomplete/malformed reads.
  - Enforce an upper bound on response sizes.
  - Avoid re-copying accumulated buffer data on every received chunk.
- R4: NVIDIA NVML Secure DLL Search Path
  - Pin `nvml.dll` loading to trusted system directory search paths (using `DefaultDllImportSearchPathsAttribute` or explicit secure path resolution) so the application does not load arbitrary unpinned DLLs from the application directory or working directory.
- Investigate existing codebase, relevant classes (e.g., `AcerAgentClient.cs`, `Nvml.cs` or NVML interop classes, hardware monitors, and test files in `NitroLite.Tests`), and propose exact technical fixes, architecture changes, and unit test strategy.

## File Paths
- Authoritative User Request: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- Output Handoff: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_2\handoff.md

## 2026-11-04T17:01:30Z
You are explorer_audit_survey_2, a read-only exploration agent.
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_2\
Read your task description in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_audit_survey_2\DISPATCH.md
Read the authoritative user request in: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md

Your scope:
Survey and investigate Requirements R3 and R4:
- R3: Acer Agent Client Framing & Robust Communication
  - Resolve packet fragmentation and timeout handling in AcerAgentClient so partial JSON responses are not prematurely returned or truncated after 25ms.
  - Prevent late-arriving bytes from contaminating subsequent command responses.
  - Close or drain connections on incomplete/malformed reads.
  - Enforce an upper bound on response sizes.
  - Avoid re-copying accumulated buffer data on every received chunk.
- R4: NVIDIA NVML Secure DLL Search Path
  - Pin nvml.dll loading to trusted system directory search paths (using DefaultDllImportSearchPathsAttribute or explicit secure path resolution) so the application does not load arbitrary unpinned DLLs from the application directory or working directory.


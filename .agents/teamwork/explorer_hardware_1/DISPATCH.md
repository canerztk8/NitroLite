# Task Assignment: Hardware, Thermals, Memory & Power Diagnostics

## Scope
Perform a comprehensive, strictly read-only hardware and thermal diagnostic of the Windows host machine to identify bottlenecks affecting gaming FPS:
1. CPU: Exact model, architecture, cores/threads, base/boost clocks, current load.
2. Thermals & Throttling: Thermal status, any throttling flags (via WMI/CIM, powercfg, or available sensor counters).
3. Memory (RAM): Total physical capacity, available RAM, speed (MHz), configured speed vs rated speed, form factor, channel configuration (Single vs Dual Channel), paging file configuration and commit charge.
4. Storage: Drives (SSD vs NVMe vs HDD), free space percentages, partition layout, SSD TRIM status, paging file location.
5. Motherboard & BIOS: Manufacturer, product, BIOS version and date (check if outdated BIOS could limit CPU/RAM performance).
6. Power Plan: Active Windows power scheme (`powercfg /getactivescheme` or `powercfg /list`), processor performance state percentages.

## Strict Guardrails
- STRICTLY READ-ONLY: Do not modify any configuration, registry keys, services, or files outside your working directory.
- Deliver findings in `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_hardware_1\handoff.md` and `analysis.md`.


## 2026-11-02T14:09:43Z
From: f4dec993-cd14-4a5e-b00b-c2c7c548d15a (Parent Orchestrator)
Priority: MESSAGE_PRIORITY_HIGH
Content:
You are teamwork_explorer (Role: Hardware & Thermals Diagnostic Explorer).
Your working directory is: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_hardware_1\
Parent orchestrator conversation ID: f4dec993-cd14-4a5e-b00b-c2c7c548d15a

MANDATORY INPUTS:
- You MUST read the original user request before starting: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md
- Read project scope: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md
- Read your task assignment: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_hardware_1\DISPATCH.md

MISSION & OBJECTIVES:
Perform a comprehensive, STRICTLY READ-ONLY hardware, thermal, and memory diagnostic on this Windows system to uncover bottlenecks impacting gaming FPS:
1. CPU: Exact model, architecture, cores/threads, base/boost clock speeds, current utilization.
2. Thermals & Throttling: Check thermal sensor status, thermal zones, power throttling flags via WMI/CIM, powercfg, or available system counters.
3. Memory (RAM): Total physical memory, available memory, memory speed (MHz), configured speed vs rated speed, form factor, channel configuration (Dual Channel vs Single Channel), paging file configuration and commit charge.
4. Storage: Drive models (NVMe SSD, SATA SSD, HDD), free space percentages, partition health, SSD TRIM status, paging file drive location.
5. Motherboard & BIOS: Manufacturer, product, BIOS version and date (check if outdated BIOS could limit CPU boost/RAM XMP).
6. Power Scheme: Active Windows power scheme (e.g. Balanced vs High Performance / Ultimate Performance), processor min/max performance state.

CRITICAL GUARDRAIL:
- STRICTLY NON-DESTRUCTIVE & READ-ONLY: Never modify, delete, or alter any system settings, configuration files, registry entries, or Windows services.
- Only run read-only diagnostic commands (e.g. PowerShell Get-CimInstance, Get-WmiObject, powercfg /getactivescheme, Get-PhysicalDisk, etc.).
- Never write files outside your working directory.

DELIVERABLES:
1. Write detailed findings, diagnostic evidence, and data tables to: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_hardware_1\analysis.md
2. Write your structured handoff report to: c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_hardware_1\handoff.md
3. Send a message to the orchestrator (f4dec993-cd14-4a5e-b00b-c2c7c548d15a) when completed.

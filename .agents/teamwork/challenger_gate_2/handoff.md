# Handoff Report — Challenger 2: Hardware Boundary & Fault Adversary (Gate 2)

**Agent:** Challenger 2 (`challenger_gate_2`)  
**Parent Agent:** `ed6fcbfa-0d62-44a8-b15f-ac1f4fac3cbc`  
**Working Directory:** `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\challenger_gate_2\`  
**Milestone:** Gate 2 Hardware Boundary & Fault Adversary Verification  
**Final Verdict:** **REJECT (REQUIRES AMENDMENT)**  

---

## 1. Observation

Direct empirical observations were gathered through live PowerShell execution, binary pattern disassembly, Windows Service Control Manager queries, and power diagnostics on the host **Acer Nitro AN16-41** (Windows 11 Build 26300.9711, BIOS V1.26):

1. **`AcerDeviceEnablingServiceV2` Active Sockets and Named Pipe**:
   - `Get-NetTCPConnection` revealed that `ADESv2Svc.exe` (PID 4712) listens on **TCP ports 51779, 51780, 51781, and 51782**.
   - `NitroSense.exe` (PID 14900) maintains an active, established socket connection to it:
     ```
     LocalAddress: 127.0.0.1:1856 | RemoteAddress: 127.0.0.1:51779 | State: Established | OwningProcess: 14900 (NitroSense.exe)
     ```
   - Named pipe query `[System.IO.Directory]::GetFiles("\\.\\pipe\\")` identified `\\.\pipe\ADESv2_Service_`.
   - Binary string extraction from `ADESv2Svc.exe` revealed JSON protocol strings:
     `"X-Sense_PrivacyPolicyConsensus_ADESV2"` and `,"request":"X-Sense_PrivacyPolicyConsensus_ADESV2"}`.
   - *Direct Contradiction:* In `nitro_sense_analysis.md`, Section 3.7 (Table 3.7) verbatim states:
     `"AcerDeviceEnablingServiceV2 | None | PnP OOBE store provisioning background worker (SWC\SWComp_ADES) for Microsoft Store installs."`

2. **Battery 80% Hold State vs. Hardware Latching Over-Claim**:
   - `C:\ProgramData\Acer\CC\settings.json` contains:
     ```json
     {
       "BIOSVersion": "V1.26",
       "WindowsVersion": "26H2 26300.9711",
       "BatteryHealthy": 1
     }
     ```
   - Querying `Win32_Battery` returned:
     `EstimatedChargeRemaining : 100` and `BatteryStatus : 2`.
   - Running `powercfg /batteryreport` and parsing the usage table revealed:
     ```
     2026-11-03 04:09:00 Suspended 80 % 63.602 mWh
     2026-11-03 11:51:03 Active AC 80 % 63.602 mWh
     2026-11-03 13:22:00 Suspended 100 % 83.637 mWh
     2026-11-03 13:23:40 Active AC 100 % 83.637 mWh
     ```
   - Binary disassembly of `AcerCCAgent.exe` identified active calls:
     `i3d::AcerHID2025::GetBatteryBoundary`, `i3d::AcerHID2025::SetTargetSystemUsageMode`, `i3d::AcerWMI::CallBIOSSetFunction`, and `i3d::ACCAgentSvis::GetCurrentBatteryHealthyConfig`.
   - *Direct Contradiction:* In `nitro_sense_analysis.md`, Section 1.4, Section 3.6.3, and Appendix C claim the 80% hold is permanently latched in hardware NVRAM across reboots and power cuts with zero dependency on `AcerCCAgent.exe`. In reality, the battery charged to 100% on AC power today at 13:22, proving that the hardware charging threshold can revert to 100% without an active software watchdog to re-apply it.

3. **WMI Provider Architecture & Zero Secret Dependency**:
   - Querying class qualifiers on `root\wmi:AcerGamingFunction` and `root\wmi:AcerGenericMethod`:
     ```
     provider = WmiProv
     WMI = True
     CLSID = {D2D588B5-D081-11d0-99E0-00C04FC2F8EC}
     ```
   - In Windows architecture, `WmiProv` is the Windows Driver Model (WDM) kernel-mode provider (`wmidx.sys`).
   - The backing driver is `AcerApplicationBaseDriver.sys` on device `ACPI\1025165F\1`.
   - `sc.exe qc` on all 9 services (`AASSvc`, `AcerLightingService`, `AcerServiceSvc`, `ASMSvc`, `AcerDIAgentSvis`, `AcerEZSvc`, `AcerDeviceInfoAgentService`, `AcerDeviceEnablingServiceV2`, `AcerCCAgentSvis`) returned:
     `DEPENDENCIES: NONE`.
   - None of the candidate Tier 1 services host any WMI provider or RPC interface needed by `AASSvc`.

4. **Dynamic Display Switching (MUX Switch) Architecture**:
   - In Windows 11, Advanced Optimus Dynamic Display Switching (DDS) is mediated directly by `nvlddmkm.sys` via ACPI DSM calls and Windows Desktop Window Manager (DWM).
   - `NVDisplay.ContainerLocalSystem` has zero SCM dependencies on any Acer service.
   - Boot MUX switching is mediated by `AASSvc` via `root\wmi:AcerGamingFunction::SetGamingMiscSetting`. Candidate Tier 1 services have zero connection to this method.

---

## 2. Logic Chain

1. **Inaccuracy in ADESv2 Characterization (Observation 1)**:
   - The deliverable report claims `AcerDeviceEnablingServiceV2` has no local ports, no IPC, and is strictly an OOBE Microsoft Store provisioning worker.
   - Observation 1 establishes that `ADESv2Svc.exe` actively listens on 4 TCP ports (51779–51782), hosts named pipe `\\.\pipe\ADESv2_Service_`, and maintains an active established socket with `NitroSense.exe` (PID 14900) to service `X-Sense_PrivacyPolicyConsensus_ADESV2`.
   - Therefore, the report's documentation in Table 3.7 and Section 4.2 contains a verifiable technical falsehood that must be corrected.

2. **Refutation of Unconditional EC Hardware Latching (Observation 2)**:
   - The report asserts that the 80% battery cap is permanently locked into the EC NVRAM, persisting across reboots, power cuts, and complete OS reinstalls, with zero need for `AcerCCAgentSvis`.
   - Observation 2 proves empirically that on this exact machine, while `C:\ProgramData\Acer\CC\settings.json` held `"BatteryHealthy": 1`, the battery reached **100% (83.637 mWh)** on AC power at 13:22.
   - Binary inspection proves `AcerCCAgent.exe` actively executes HID/WMI commands (`SetTargetSystemUsageMode`, `CallBIOSSetFunction`) to re-assert the boundary.
   - Therefore, the EC threshold can and does reset to 100% under certain power/firmware states. Disabling `AcerCCAgentSvis` strips the system of the background overseer that re-programs the 80% boundary, invalidating the claim of zero risk.

3. **Robustness of Core Hardware Cooling & MUX Isolation (Observations 3 & 4)**:
   - Observation 3 confirms that `AcerGamingFunction` is hosted directly by the kernel driver (`WmiProv`), not by any user-mode service.
   - Observation 4 confirms that dynamic MUX switching is handled natively by the NVIDIA driver and DWM.
   - SCM dependency queries confirm zero cross-service dependencies between Tier 1 services and `AASSvc`.
   - Therefore, disabling the 6 Tier 1 candidate services remains physically safe for cooling fans, thermal modes, and GPU switching.

---

## 3. Caveats

1. **Elevation Requirement**:
   Service control operations (`Stop-Service`, `Set-Service`) require an elevated Administrator token (`BUILTIN\Administrators` or `NT AUTHORITY\SYSTEM`). When run from a standard user token, SCM returns Win32 error code 5 (`ERROR_ACCESS_DENIED`). All debloat and rollback scripts must be run from an elevated PowerShell console (`Run as Administrator`).
2. **NitroSense Tolerance to ADESv2 Absence**:
   While `NitroSense.exe` has an established TCP socket to `ADESv2Svc.exe` on port 51779, temporary suspension of `ADESv2Svc.exe` does not immediately crash the NitroSense UI; however, NitroSense will log socket connection retries.
3. **No Code Modification**:
   As a reviewer/challenger agent, no direct modifications to `nitro_sense_analysis.md` were performed. All corrections are detailed in `challenge.md` for the author to integrate.

---

## 4. Conclusion

**Final Verdict: REJECT (REQUIRES AMENDMENT)**

While the practical debloat prescription (stopping the 6 Tier 1 services) is safe for cooling fans and thermal regulation, `nitro_sense_analysis.md` cannot be approved in its current state due to two technical deficiencies:
1. **Factually False IPC Claim**: Table 3.7 and Section 4.2 claim `AcerDeviceEnablingServiceV2` has `Local Port / IPC: None`. It actually listens on ports 51779–51782 and maintains an active established socket with `NitroSense.exe`.
2. **Unverified Battery Persistence Over-Claim**: Section 1.4, Section 3.6.3, and Appendix C claim the 80% battery cap is unconditionally hardware-latched in EC NVRAM with zero software dependency. Live telemetry proved the battery charged to 100% today at 13:22 despite `BatteryHealthy: 1`, proving that `AcerCCAgentSvis` is necessary to monitor and re-assert the boundary.

**Required Actions for Approval:**
- Amend Table 3.7 and Section 4.2 with accurate socket/pipe and privacy consensus details for `AcerDeviceEnablingServiceV2`.
- Amend Section 1.4, Section 3.6.3, and Section 4.3 to reflect the true role of `AcerCCAgentSvis` in re-asserting the 80% battery boundary and disclose the risk of charging to 100% if the service is disabled.

---

## 5. Verification Method

To independently reproduce and verify these adversarial findings on the live machine:

```powershell
# 1. Verify ADESv2 active socket listener and NitroSense connection:
Get-NetTCPConnection -LocalPort 51779 | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess

# 2. Verify ADESv2 named pipe:
[System.IO.Directory]::GetFiles("\\.\\pipe\\") | Where-Object { $_ -match "ADES" }

# 3. Verify battery 100% charge state vs settings.json BatteryHealthy: 1:
Get-Content "C:\ProgramData\Acer\CC\settings.json"
Get-CimInstance Win32_Battery | Select-Object Name, EstimatedChargeRemaining, BatteryStatus

# 4. Inspect battery history proving 100% charge jump at 13:22:
powercfg /batteryreport /output ".\verify-battery.html"
Select-String -Path ".\verify-battery.html" -Pattern "2026-11-03 13:22" -Context 0, 2

# 5. Verify WMI provider provenance (WmiProv kernel model):
(Get-CimClass -Namespace "root\wmi" -ClassName "AcerGamingFunction").CimClassQualifiers | Where-Object { $_.Name -in "provider", "WMI" }
```

**Invalidation Conditions:**
- If `ADESv2Svc.exe` has no listening ports or socket connections from `NitroSense.exe`, Challenge 1 is invalidated.
- If the battery never charged to 100% while `"BatteryHealthy": 1` was configured, Challenge 2 is invalidated.

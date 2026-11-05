# Hardware, Thermals, Memory & Power Architecture Analysis

**Host System**: Acer Nitro AN16-41 (Gaming Laptop)  
**Operating System**: Microsoft Windows 11 Home (64-bit, Build 26300)  
**Investigation Mode**: Strictly Read-Only Diagnostic Inspection  
**Date of Analysis**: November 2, 2026  

---

## 1. Executive Summary & Hardware Health Score

| Subsystem | Ground-Truth Specification | Health / Config State | Gaming Bottleneck Rating |
|:---|:---|:---|:---:|
| **CPU** | AMD Ryzen 7 7840HS (8C/16T, Zen 4, 3.8–5.1 GHz, TSMC 4nm) | Optimal boost, 0 hardware/WHEA errors | **LOW** (Excellent CPU) |
| **Thermals & Throttling** | ACPI `\_TZ.TZ01`: ~72–73°C at light load; RTX 4070: 58°C at idle | Elevated baseline idle thermals due to power plan | **MEDIUM** (Thermal headroom risk) |
| **Memory (RAM)** | 32 GB (2x16GB) Crucial/Micron DDR5-5600 SODIMM (Dual Channel) | 5600 MT/s rated & configured; 18.2 GB free | **NONE** (Optimal configuration) |
| **Storage** | 2TB Samsung 990 PRO NVMe + 1TB Samsung PM9A1 NVMe PCIe 4.0 | TRIM enabled, 4KB aligned, 74% / 82% free space | **NONE** (Top-tier storage) |
| **Motherboard & BIOS** | Acer PHX Sierra_PEH, INSYDE BIOS V1.26 | Secure Boot active; UEFI GOP valid | **LOW** (Up to date firmware) |
| **Power Profile** | Windows "Ultimate Performance" Scheme + Best Performance Overlay | Min processor state pinned at 100% on AC | **HIGH** (Causes elevated idle heat) |
| **Virtualization/Security** | VBS (Virtualization-Based Security) & HVCI (Memory Integrity) | Active (Security Services Running: {2}) | **MEDIUM** (3–8% CPU gaming overhead) |

---

## 2. Processor (CPU) Diagnostic

### 2.1 Hardware Identification & Topology
- **Model**: `AMD Ryzen 7 7840HS w/ Radeon 780M Graphics`
- **Architecture**: AMD Zen 4 (Phoenix), 4nm FinFET (TSMC)
- **Family / Stepping**: Family 25, Model 116, Stepping 1 (Socket FP8 BGA)
- **Physical Cores**: 8
- **Logical Processors**: 16 (Simultaneous Multithreading / SMT active)
- **Base Frequency**: 3,801 MHz (3.8 GHz)
- **Maximum Boost Frequency**: Up to 5.1 GHz
- **Cache Topology**:
  - **L1 Cache**: 512 KB total (8 Cores × 64 KB per core [32 KB Data + 32 KB Instruction])
  - **L2 Cache**: 8,192 KB total (8 Cores × 1,024 KB dedicated per core)
  - **L3 Cache**: 16,384 KB (16 MB shared monolithic Zen 4 CCX pool)

### 2.2 CPU Operational Telemetry
- **Idle/Light Load Utility**: 8% – 15% processor utility across background tasks.
- **Clock Scaling**: Processor frequency counter reports `99% – 100% of Maximum Frequency` even under minimal load due to `PROCTHROTTLEMIN = 100%`.
- **Core Parking**: `\Processor Information(_Total)\Parking Status = 0` (All 16 logical threads unparked).
- **C-State Residency**: Under current settings, cores spend 5.9% in C1, 41.7% in C2, and 29.4% in C3.
- **Hardware Reliability**: Zero WHEA-Logger errors, zero CPU internal parity errors.

---

## 3. Thermal Status & Throttling Diagnostics

### 3.1 Sensor Readings
- **CPU Thermal Zone**:
  - Counter: `\Thermal Zone Information(\_TZ.TZ01)\Temperature`
  - Current Temperature: `345 – 346 K` (**71.85°C – 72.85°C**) at 12%–21% CPU utility.
  - High Precision Temperature reading: `3402` (67.0°C – 73.0°C range).
- **dGPU Temperature (NVIDIA GeForce RTX 4070 Laptop GPU)**:
  - GPU Core Temperature: **58°C** at idle / light desktop workload.
  - Target Temperature Specification: **87°C** (thermal throttle threshold).
  - Idle Power Draw: **18.14 W** (Default TGP baseline: 80W, Max TGP: 140W with Dynamic Boost).

### 3.2 Throttling Flag Verification
```
WMI / Performance Counter Throttling Checks:
- Win32_PerfFormattedData_Counters_ThermalZoneInformation:
    ThrottleReasons      : 0 (No ACPI thermal throttling)
    PercentPassiveLimit  : 100% (Not thermally capped)
- NVIDIA-SMI Clock Event Reasons:
    SW Thermal Slowdown  : Not Active (0 us)
    HW Thermal Slowdown  : Not Active (0 us)
    HW Power Braking     : Not Active (0 us)
    SW Power Cap         : Not Active (59.7 ms transient)
- Windows Kernel-Processor-Power Event Log:
    Event ID 37 (Speed limited by firmware) : 0 occurrences
    Event ID 86 (Thermal emergency shutdown): 0 occurrences
```

### 3.3 Thermal Assessment
While there is **no active thermal throttling** occurring at this moment, the baseline idle temperature of **~72°C on the CPU** inside a gaming laptop chassis is notably elevated. In sustained gaming sessions where both the CPU and the 140W RTX 4070 dGPU are generating heat across shared heat pipes, high baseline temperatures shorten the time before thermal limits (100°C on Zen 4, 87°C on RTX 4070) are reached, leading to eventual clock degradation and micro-stuttering.

---

## 4. Memory (RAM) Subsystem Diagnostic

### 4.1 Physical DIMM Configuration
The host machine is equipped with two matched high-speed DDR5 SODIMM modules:

| Slot / Locator | Manufacturer | Part Number | Capacity | Rated Speed | Configured Speed | Form Factor | Type |
|:---|:---|:---|:---:|:---:|:---:|:---:|:---:|
| **P0 CHANNEL A (DIMM 0)** | Micron Technology | `CT16G56C46S5.C8B2` | 16 GB | 5600 MT/s | 5600 MT/s | SODIMM (12) | DDR5 (34) |
| **P0 CHANNEL B (DIMM 0)** | Micron Technology | `CT16G56C46S5.C8B2` | 16 GB | 5600 MT/s | 5600 MT/s | SODIMM (12) | DDR5 (34) |

- **Total Physical Installed**: 34,359,738,368 bytes (32.0 GB).
- **Total Visible to OS**: 32,830,324 KB (~31.31 GB; ~700 MB reserved for hardware and Radeon 780M iGPU frame buffer).
- **Channel Mode**: **Dual Channel** (128-bit memory bus / 4 × 32-bit DDR5 subchannels).
- **Speed Compliance**: Running at full JEDEC **5600 MT/s** (no RAM underclocking or downclocking to 4800 MT/s).

### 4.2 Utilization, Commit Charge & Paging File
- **Physical Memory in Use**: ~13.1 GB (Working sets + Standby/Modified cache).
- **Free Physical Memory**: **19,096,372 KB (~18.21 GB free)**.
- **Commit Limit (Total Virtual Memory)**: 35,844,980 KB (~34.18 GB).
- **Current Commit Charge**: ~15.98 GB (46.7% commit utilization).
- **Paging File Location & Size**:
  - File: `C:\pagefile.sys` (hosted on Samsung 990 PRO 2TB NVMe).
  - Allocated Base Size: 2,944 MB (~2.88 GB).
  - Current Usage: 214 MB (7.2%).
  - Peak Usage: 283 MB (9.6%).
  - Management Mode: Automatic / System-Managed (`?:\pagefile.sys`).

**Conclusion on RAM**: The memory configuration is flawless. 32 GB of dual-channel DDR5-5600 provides ample bandwidth (~89.6 GB/s theoretical) and plenty of memory headroom for modern 2026 AAA titles (which typically consume 12–20 GB total system RAM).

---

## 5. Storage Subsystem & Drive Health

### 5.1 Physical Drives & Partitions

| Drive # | Model | Interface / Bus | Media Type | Capacity | Partition Layout | Health Status |
|:---|:---|:---:|:---:|:---:|:---|:---:|
| **Disk 0** | **Samsung SSD 990 PRO 2TB** | NVMe (PCIe 4.0 x4) | SSD | 2,000.4 GB | EFI (200MB), MSR (16MB), OS `C:` (1.82TB), Recovery (886MB) | **Healthy / OK** |
| **Disk 1** | **SAMSUNG MZVL21T0HCLR-00B07** (OEM PM9A1) | NVMe (PCIe 4.0 x4) | SSD | 1,024.2 GB | Data `D:` (953.9GB) | **Healthy / OK** |

### 5.2 Volume Free Space & TRIM Status

| Volume | Label | File System | Total Size | Free Space | Free Space % | TRIM Status | Alignment |
|:---:|:---|:---:|:---:|:---:|:---:|:---:|:---:|
| **C:** | Windows | NTFS | 1,862.0 GB (1.82 TB) | 1,384.3 GB | **74.3% Free** | Enabled (`DisableDeleteNotify=0`) | 4KB Aligned (Offset 227,540,992 B) |
| **D:** | New Volume | NTFS | 953.9 GB | 780.8 GB | **81.9% Free** | Enabled (`DisableDeleteNotify=0`) | 4KB Aligned (Offset 1,048,576 B) |

### 5.3 Storage Event Logs & Latency Guardrails
- **TRIM**: Both NTFS and ReFS TRIM commands are active, preventing SSD write-amplification and write performance degradation.
- **I/O Errors**: Zero error events for Disk 0 or Disk 1 in `stornvme` or `disk` logs. Historical event log showed errors only for a disconnected external drive (`Harddisk2`).
- **Disk Power Policy**: `DISKIDLE = 0` (Disks never spin down or enter low power sleep), eliminating NVMe wake-up latency during asset streaming in open-world games.

---

## 6. Motherboard & BIOS Diagnostics

- **Computer Manufacturer**: Acer
- **Model**: Nitro AN16-41
- **Chassis Type**: 10 (Notebook)
- **Motherboard (Baseboard)**: PHX Sierra_PEH (Version V1.26)
- **BIOS Brand & Version**: INSYDE Corp. V1.26 (ACRSYS - 2)
- **BIOS Release Date**: June 4, 2026
- **Embedded Controller (EC)**: Version 1.11
- **Secure Boot State**: `UEFISecureBootEnabled: 1` (Active and verified)
- **Firmware Status Assessment**:
  - BIOS V1.26 is a recent release containing AMD AGESA microcode for Phoenix APUs and fan control updates.
  - Acer published subsequent firmware builds (V1.27+) to refine Secure Boot certificate validations and thermal fan acoustics.

---

## 7. Power Scheme & Windows Performance Subsystem

### 7.1 Active Power Plan Breakdown
The system is currently configured with the **Ultimate Performance** power scheme:
- **Active Power Scheme GUID**: `dd8776a4-18f1-47c0-9022-128ca6c59f85` (Ultimate Performance)
- **Windows 11 AC Overlay GUID**: `ded574b5-45a0-4f42-8737-46345c09c238` (Best Performance)

Detailed Processor & Peripheral Parameters:
- **Minimum Processor State (`PROCTHROTTLEMIN`)**:
  - AC Power: **100% (`0x00000064`)**  *(Crucial finding!)*
  - DC Power: **5% (`0x00000005`)**
- **Maximum Processor State (`PROCTHROTTLEMAX`)**:
  - AC Power: **100% (`0x00000064`)**
  - DC Power: **100% (`0x00000064`)**
- **PCI Express Link State Power Management (ASPM)**:
  - AC Power: **0x0 (Off)** — ensures full PCIe bus bandwidth to RTX 4070 and NVMe SSDs without latency states.
- **Turn Off Hard Disk Timeout**:
  - AC Power: **0 seconds (Never)**.

### 7.2 Windows Gaming & Security Configurations
- **Hardware-Accelerated GPU Scheduling (HAGS)**: `HwSchMode = 2` (**Enabled**). Essential for NVIDIA DLSS 3 Frame Generation and low-latency driver scheduling.
- **Windows Game Mode**: `AutoGameModeEnabled = 1` (**Enabled**). Prioritizes foreground gaming threads and deprioritizes background tasks.
- **GameDVR Background Recording**: `GameDVR_Enabled = 0` (**Disabled**). Excellent — prevents background GPU video encoder overhead and disk writes.
- **Virtualization-Based Security (VBS)**: `VirtualizationBasedSecurityStatus = 2` (**Enabled & Running**).
- **Hypervisor-Protected Code Integrity (HVCI / Memory Integrity)**: `SecurityServicesRunning = {2}` (**Active**).

---

## 8. Bottleneck Identification & Optimization Analysis

Based on our empirical observations, the system possesses outstanding raw hardware (Zen 4 7840HS + RTX 4070 + 32GB DDR5-5600 + Samsung 990 PRO). However, four distinct configuration bottlenecks exist:

### Bottleneck A: Aggressive `PROCTHROTTLEMIN = 100%` Inducing Thermal Spikes (High Priority)
- **The Issue**: In the "Ultimate Performance" plan, setting the Minimum Processor State to 100% instructs Windows to prevent the AMD Zen 4 cores from entering idle frequency states. 
- **The Impact**: CPU thermal zone is idling at **~72°C–73°C** even with only 12–20% load. In modern laptop APUs with unified vapor chambers / heat pipes, a hot CPU reduces the dynamic thermal headroom available to both the CPU boost clocks and the RTX 4070's Dynamic Boost (up to 140W).
- **Recommendation**: Switch the Minimum Processor State on AC power to **5%** (as standard in Balanced/High Performance plans) while keeping Maximum Processor State at **100%**. This permits Zen 4 cores to downclock instantly during pauses and idle frames, dropping baseline temperatures by 10–15°C and preserving PPT/TDP thermal headroom for peak gaming boost clocks.

### Bottleneck B: Virtualization-Based Security (VBS / HVCI) Overhead (Medium Priority)
- **The Issue**: VBS and Memory Integrity (`HVCI`) are active (`VirtualizationBasedSecurityStatus = 2`, `SecurityServicesRunning = {2}`).
- **The Impact**: While HVCI provides kernel code protection against malware, it introduces hardware virtualization SLAT and nested page table overhead. In competitive CPU-bound games (e.g., Counter-Strike 2, Valorant, Rainbow Six Siege, Apex Legends), HVCI is documented to reduce average FPS by **3% to 8%** and increase 1% low frame time variance.
- **Recommendation**: Provide the user with clear instructions on toggling "Memory Integrity" in Windows Security (`Core isolation -> Memory integrity`) when seeking absolute maximum competitive FPS, with clear security trade-off disclosure.

### Bottleneck C: Discrete GPU Desktop VRAM Residency & Performance State P0 (Medium Priority)
- **The Issue**: `nvidia-smi` confirms the RTX 4070 is active on desktop (`Disp.A = On`) and locked in Performance State `P0` (1980 MHz core, 7851 MHz memory) drawing 18.14W at 58°C, with **1.89 GB of VRAM** already consumed by background applications (Discord, Firefox, Steam, Spotifast, Zapfast, NitroSense).
- **The Impact**: On an 8GB VRAM GPU, starting a AAA game (e.g., Cyberpunk 2077, Hogwarts Legacy, Forza Horizon 5) with 1.9 GB already consumed by background desktop apps leaves only ~6.1 GB for game textures, frame buffers, and ray tracing, precipitating VRAM spillover into system RAM and massive frame drops/stuttering.
- **Recommendation**: Configure background apps (Discord, Firefox, Spotify) to either disable hardware acceleration or close during gaming, and ensure NVIDIA Power Management Mode is set to "Optimal Power" or "Normal" for desktop workloads.

### Bottleneck D: Acer OEM Telemetry & Service Clutter (Low-Medium Priority)
- **The Issue**: 7 distinct Acer background services (`Acer Care Center`, `Acer Device Enabling Service V2`, `Acer Experience Zone`, `Acer Lighting`, etc.) plus multiple instances of `NitroSense` run continuously in the background.
- **The Impact**: While negligible on an 8-core CPU during idle, periodic background telemetry polling from multiple OEM agents can interrupt the Windows thread scheduler during time-critical frame presentation loops (causing 1% low FPS dips).
- **Recommendation**: User can safely disable non-essential Acer background telemetry and update NitroSense fan curves.

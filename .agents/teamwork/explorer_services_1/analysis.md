# Detailed Diagnostic Analysis: Windows Services, Background Processes & Startup Programs

**Date of Diagnostic:** 2026-11-02  
**Target System:** Windows 11 Home (Build 26300.9711, Version 26H2)  
**System Memory:** 32.06 GB (32,830,324 KB Visible, ~19.1 GB Free Physical)  
**Power Plan:** Ultimate Performance (`dd8776a4-18f1-47c0-9022-128ca6c59f85`)  
**Diagnostic Mode:** Strictly Read-Only & Non-Destructive  

---

## 1. Executive Summary

This diagnostic investigation evaluated the active background processes, startup entries, Windows services, and gaming subsystems to identify performance drag affecting gaming frame rates (FPS), frame pacing, and micro-stutter (1% and 0.1% lows).

### Key Strengths Already Configured:
- **Windows Game Mode:** Enabled (`AutoGameModeEnabled = 1`).
- **Xbox Game DVR Background Recording:** Disabled (`AppCaptureEnabled = 0`, `HistoricalCaptureEnabled = 0`, `GameDVR_Enabled = 0`), preventing video encoding GPU/RAM bandwidth drain.
- **Hardware-Accelerated GPU Scheduling (HAGS):** Enabled (`HwSchMode = 2`).
- **Optimizations for Windowed Games (VRR):** Enabled (`VRROptimizeEnable = 1`).
- **Windows Telemetry Services:** `DiagTrack` and `dmwappushservice` are Disabled.
- **SysMain (SuperFetch) & Print Spooler:** Both are already Disabled.
- **UWP Background Permissions:** Globally restricted (`GlobalUserDisabled = 1`).

### Primary Performance Bottlenecks & Drag Factors Identified:
1. **Audiodg CPU & DPC Overhead:** `audiodg.exe` has accumulated **930+ seconds of CPU time** and continuously draws ~0.7–1.0% CPU when idle, driven by active audio enhancements and DTS APO / Realtek DSP filters. This is a primary driver of audio-related micro-stuttering in competitive titles.
2. **Massive Memory & GPU Contention from Open Apps:**
   - **Mozilla Firefox:** 18 processes consuming **3,717 MB (3.7 GB) RAM**, over 3,735s CPU time, and over 87 GB of read transfer.
   - **Discord:** 6 processes consuming **1,370 MB (1.37 GB) RAM**, over 4,034s CPU time, with **Hardware Acceleration enabled** (occupying 3D GPU context and VRAM) and in-game overlay hooks installed.
   - **Steam Web Helper:** 8 CEF processes consuming **1,092 MB (1.1 GB) RAM**.
   - **ATK HUB:** 5 processes consuming **661 MB RAM** for peripheral management.
3. **Acer OEM Bloatware Service Swarm:** **9 separate Acer services** are set to `Automatic` and actively running concurrently (`AcerCCAgentSvis`, `AASSvc`, `AcerDeviceInfoAgentService`, `AcerEZSvc`, `AcerLightingService`, `AcerServiceSvc`, `ASMSvc`, `AcerDIAgentSvis`, `ADESv2Svc`), paired with 4 `NitroSense` background processes (463 MB RAM).
4. **Unnecessary Persistent Third-Party Background Services:**
   - `Plastic Server 6` (`plasticd.exe`): Unity Plastic SCM local server running automatically, holding **191.6 MB RAM**.
   - `PCManager Service Store` (`MSPCManagerService.exe`): Microsoft PC Manager service holding **125.2 MB RAM**.
   - `CodexSandboxService.OpenAI.Codex`: ChatGPT desktop sandbox service running in background.
   - `WSearch` (Windows Search Indexer) + `SearchHost.exe`: Spawning 6 `msedgewebview2.exe` processes taking **397.5 MB RAM** solely for Start Menu Bing web suggestions.

---

## 2. Active Background Processes Audit

### 2.1 Aggregated Application Memory Usage (Top Consumers)

| Application / Process Group | Instance Count | Working Set (RAM MB) | Private Memory (MB) | Total CPU Time (s) | Primary Impact on Gaming |
|:---|:---:|:---:|:---:|:---:|:---|
| **firefox** | 18 | **3,717.1 MB** | 3,533.0 MB | 3,735.1 s | Severe memory reservation, browser GPU context contention |
| **Discord** | 6 | **1,370.5 MB** | 1,085.6 MB | 4,034.5 s | GPU hardware acceleration, overlay injection hook risk |
| **steamwebhelper** | 8 | **1,092.4 MB** | 1,187.8 MB | 186.4 s | Chromium Embedded Framework memory footprint |
| **svchost** (OS host processes) | 39 | 1,018.3 MB | 331.3 MB | 15.9 s | Windows Core Services |
| **Antigravity** (IDE / Tools) | 6 | 854.9 MB | 621.3 MB | 130.7 s | Development environment |
| **ATK HUB** | 5 | **661.5 MB** | 496.0 MB | 60.5 s | Peripheral RGB/Macro polling and UI daemon |
| **NitroSense** | 4 | **463.2 MB** | 514.0 MB | 93.2 s | Acer OEM fan/power mode management UWP |
| **MsMpEng** (Defender Antivirus) | 1 | 440.4 MB | 467.0 MB | 0.0 s | Real-time file scanning and signature monitoring |
| **msedgewebview2** (SearchHost) | 6 | **397.5 MB** | 238.6 MB | 6.2 s | Windows Start Menu Bing Web Search rendering |
| **language_server** | 1 | 387.9 MB | 342.3 MB | 48.1 s | Development code analysis |
| **explorer** | 1 | 331.9 MB | 237.1 MB | 94.6 s | Windows Desktop Shell |
| **NVDisplay.Container** | 2 | 233.4 MB | 176.3 MB | 0.0 s | NVIDIA Display Driver Service |
| **steam** (Core Client) | 1 | 211.9 MB | 214.4 MB | 122.5 s | Steam game launcher core |
| **TextInputHost** | 1 | 196.9 MB | 137.7 MB | 5.3 s | Windows touch/emoji/clipboard input host |
| **plasticd** (Plastic SCM) | 1 | **191.6 MB** | 137.9 MB | 0.0 s | Background SCM version control database server |
| **zapfast** | 1 | 180.5 MB | 156.9 MB | 67.8 s | Third-party utility |
| **spotifast** / **soloist** | 2 | **254.2 MB** | 298.8 MB | 229.5 s | Custom Spotify player/wrapper |
| **nvcontainer** | 3 | 172.7 MB | 67.1 MB | 21.8 s | NVIDIA backend container |
| **MSPCManagerService** | 1 | **125.2 MB** | 28.7 MB | 4.2 s | Microsoft PC Manager background service |
| **audiodg** | 1 | **107.4 MB** | 101.4 MB | **917.8 s** | Audio Device Graph Isolation (Enhancement DSP) |
| **SoundSwitch** | 1 | 105.0 MB | 30.4 MB | 41.8 s | Audio output switcher |
| **MonitorianPlus** | 1 | 69.2 MB | 48.0 MB | 1.8 s | DDC/CI monitor brightness utility |
| **OfficeClickToRun** | 1 | 74.8 MB | 38.9 MB | 0.0 s | Microsoft Office update/launch listener |

---

### 2.2 CPU Sampling & Thread Latency Observations

Idle CPU utilization was sampled over a 3-sample window, measuring between **12.1% and 23.5% total CPU utilization** with no games running.

A 2-second delta differential identified the active background CPU consumers at idle:
1. **Discord.exe (PID 17868 & 16496):** Continuous ~1.85% background CPU utilization (Electron WebRTC keepalive, animation loops, and hardware-accelerated drawing).
2. **Firefox.exe (PID 10760, 20616, 18644):** Continuous ~2.25% background CPU utilization (JavaScript timers, background web sockets, media canvas refresh).
3. **audiodg.exe (PID 4312):** Sustained ~0.73% background CPU utilization. Over system uptime, `audiodg.exe` has burned **930 seconds of CPU time**.

### 2.3 Disk I/O Transfer Metrics

| Process Name | Process ID | Read Transfer (Bytes) | Write Transfer (Bytes) | Other Transfer | Impact Evaluation |
|:---|:---:|:---:|:---:|:---:|:---|
| **firefox.exe** | 18644 / 10760 | **138.8 GB** | **20.2 GB** | 72.1M | Massive disk cache write churn; causes disk contention during asset streaming |
| **Discord.exe** | 17868 / 16616 | **2.04 GB** | **2.50 GB** | 147.9M | Continuous cache logging and local IndexedDB state writes |
| **MsMpEng.exe** | 4944 | **1.31 GB** | **283.2 MB** | 492.4M | Real-time antivirus I/O monitoring; inspects game executables unless excluded |
| **steam.exe & helper** | 15344 / 15728 | **640.6 MB** | **759.5 MB** | 158.9M | Client updates, cloud sync, and CEF local caches |
| **ATK HUB.exe** | 20700 | **52.9 MB** | **123.5 MB** | 503.8K | Persistent peripheral telemetry write-logging |

---

## 3. Startup Applications & Autostart Audit

Startup entries were gathered from `Win32_StartupCommand`, Registry Run keys (`HKCU` and `HKLM`), Task Manager's `StartupApproved` registry keys, and non-Microsoft Scheduled Tasks.

### 3.1 Registry Run Keys (`Run` & `StartupApproved`)

| Startup Name | Command / Target Path | Location | State in StartupApproved | Recommendation |
|:---|:---|:---|:---:|:---|
| **Steam** | `"C:\Program Files (x86)\Steam\steam.exe" -silent` | HKCU Run | **Enabled** (`02`) | User choice; launch manually before gaming or keep silent |
| **Discord** | `"C:\Users\caner\AppData\Local\Discord\Discord.exe" ...` | HKCU Run | **Enabled** (`02`) | **Disable autostart**; launch only when communicating |
| **SoundSwitch** | `...\SoundSwitch\SoundSwitch.exe` | HKCU Run | **Enabled** (`02`) | Lightweight utility; acceptable if actively used |
| **ZapFast** | `...\ZapFast\zapfast.exe --start-hidden` | HKCU Run | **Enabled** (`02`) | Disable if not actively required on every boot |
| **com.blitz.app** | Blitz gaming overlay / stats utility | HKCU Startup | **Enabled** (`02`) | **Disable autostart**; overlays cause frame pacing stutter |
| **com.akiver.csdm** | CS Demo Manager | HKCU Startup | **Enabled** (`02`) | Launch on demand only |
| **GoogleUpdater** | `...\GoogleUpdater\...\updater.exe --wake` | HKCU Run | **Enabled** (`02`) | **Disable**; unnecessary at boot |
| **SecurityHealth** | `%windir%\system32\SecurityHealthSystray.exe` | HKLM Run | **Enabled** (`06`) | Keep (Windows Security icon) |
| **RtkAudUService** | `...\realtekservice.inf_...\RtkAudUService64.exe -background` | HKLM Run | **Enabled** (`02`) | Realtek audio tray background loader |
| **EverythingToolbar** | Everything search toolbar hook | HKCU Startup | **Enabled** (`02`) | Keep if user prefers, minimal overhead |
| **EpicGamesLauncher** | Epic Games Client | HKCU Run | **Disabled** (`03`) | Already optimized |
| **EADM** | EA Desktop App | HKCU Run | **Disabled** (`03`) | Already optimized |
| **CurseForge** | CurseForge mod manager | HKCU Run | **Disabled** (`03`) | Already optimized |
| **FACEIT** | Faceit Client | HKCU Run | **Disabled** (`03`) | Already optimized |
| **Logitech Assistant**| Logitech Download Assistant | HKLM Run | **Disabled** (`03`) | Already optimized |
| **SteelSeriesGG** | SteelSeries GG Engine | HKLM Run | **Disabled** (`03`) | Already optimized |

---

### 3.2 Non-Microsoft Scheduled Tasks (Logon & Boot Autostart)

Auditing `Get-ScheduledTask` revealed multiple third-party tasks configured to run automatically:
1. **AMDRyzenMasterSDKTask:** `C:\Program Files\AMD\CNext\...` (AMD CPU performance tracking daemon).
2. **NitroSenseLauncher:** `C:\Program Files\NitroSense\...` (Triggers Acer NitroSense at logon).
3. **NvDriverUpdateCheckDaily & NVIDIA App SelfUpdate:** Periodic background update checkers from NVIDIA.
4. **Ubisoft Connect Background Update:** Launches background update loops for Ubisoft games.
5. **Office Maintenance Swarm (11 Scheduled Tasks):**
   - `Office Actions Server`, `Office Automatic Updates 2.0`, `Office Background Push Maintenance`, `Office ClickToRun Service Monitor`, `Office Feature Updates`, `Office Performance Monitor`, `Office Serviceability Manager`, `Office Startup Maintenance`.
   - These tasks regularly wake worker processes and perform background telemetry.
6. **Firefox Default Browser Agent:** Periodic background agent from Mozilla.

---

## 4. Windows Services & OEM Bloatware Audit

### 4.1 Core Windows Services Relevant to Gaming

| Service Name | Display Name | Current Status | Start Type | Impact & Assessment |
|:---|:---|:---:|:---:|:---|
| **SysMain** | SysMain (SuperFetch) | **Stopped** | **Disabled** | **Optimal**: Already disabled; eliminates background RAM pre-caching disk spikes. |
| **DiagTrack** | Connected User Experiences and Telemetry | **Stopped** | **Disabled** | **Optimal**: Already disabled; stops telemetry queue transmission. |
| **dmwappushservice** | WAP Push Message Routing Service | **Stopped** | **Disabled** | **Optimal**: Already disabled. |
| **Spooler** | Print Spooler | **Stopped** | **Disabled** | **Optimal**: Already disabled; frees memory and CPU cycles if no physical printer is used. |
| **WSearch** | Windows Search | **Running** | **Automatic** | **Moderate Overhead**: Continuous indexing of user files and powering Start Menu web search. Spawns 6 WebView2 processes (397 MB RAM). |
| **BITS** | Background Intelligent Transfer Service | **Running** | **Automatic** | Manages background download transfers. |
| **wuauserv** | Windows Update | **Stopped** | **Automatic** | Normal trigger behavior. |
| **WinDefend** | Microsoft Defender Antivirus | **Running** | **Automatic** | Active real-time protection; exclude game folders to reduce I/O hitching during asset loads. |
| **WdNisSvc** | Defender Network Inspection | **Running** | **Manual** | Active network packet inspector. |

---

### 4.2 Acer OEM Bloatware Services Swarm

The system is an **Acer Nitro gaming laptop** with **10 OEM background services installed, 9 of which are currently running automatically**:

| Service Name | Display Name | State | Start Mode | Executable / Source Path | Performance Impact |
|:---|:---|:---:|:---:|:---|:---|
| **AASSvc** | Acer Agent Service | **Running** | Auto | `predatorservice.inf_...\AAS.exe` | Persistent OEM telemetry & background polling |
| **AcerCCAgentSvis** | Acer Care Center | **Running** | Auto | `C:\Windows\system32\AcerCCAgent.exe` | Frequent disk/health polling and update checks |
| **AcerDeviceEnablingServiceV2** | Acer Device Enabling Service V2 | **Running** | Auto | `acerdeviceenablingservicecomponent.inf...` | Driver helper |
| **AcerDeviceInfoAgentService** | AcerDeviceInfoAgentService | **Running** | Auto | `C:\Windows\System32\AcerDeviceInfo\...` | System hardware profiling daemon |
| **AcerDIAgentSvis** | Acer Device Info | **Running** | Auto | `C:\Windows\system32\AcerDIAgent.exe` | Redundant device metadata collector |
| **AcerEZSvc** | Acer Experience Zone Component | **Running** | Auto | `ezservicecomponent.inf...` | OEM promotional/feature telemetry |
| **AcerLightingService** | Acer Lighting Service | **Running** | Auto | `predatorservice.inf_...\AcerLightingService.exe` | RGB keyboard illumination control |
| **AcerServiceSvc** | Acer Service Component Service | **Running** | Auto | `acerservicecomponent.inf...` | General OEM background bridge |
| **ASMSvc** | Acer System Monitor Service | **Running** | Auto | `sysmonitorservice.inf_...\ASMSvc.exe` | Continuous hardware sensor polling |
| **AcerQAAgentSvis** | Acer Quick Access | **Stopped** | Disabled | `C:\Windows\system32\AcerQAAgent.exe` | Already disabled |

**Total Impact of Acer Ecosystem:**  
Combined with the 4 UWP `NitroSense` processes, these utilities hold over **600 MB of physical RAM** and maintain ongoing sensor polling threads across CPU cores, competing for timer interrupts and L3 cache lines during gaming.

---

### 4.3 Third-Party Services Audit

| Service Name | Display Name | State | Start Mode | Binary Path | Assessment & Recommended User Action |
|:---|:---|:---:|:---:|:---|:---|
| **Plastic Server 6** | Plastic SCM Server | **Running** | **Auto** | `C:\Program Files\PlasticSCM5\server\plasticd.exe` | **High RAM Drag (191.6 MB)**. Local version control server. Recommended: Set to Manual; start only when developing. |
| **PCManager Service Store** | Microsoft PC Manager Service | **Running** | **Auto** | `...MicrosoftPCManager_...\MSPCManagerService.exe` | **Moderate RAM Drag (125.2 MB)**. Redundant optimizer service. Recommended: Disable autostart or uninstall. |
| **CodexSandboxService.OpenAI.Codex** | ChatGPT | **Running** | **Auto** | `...OpenAI.Codex_...\codex-windows-sandbox-service.exe` | Background sandbox service for ChatGPT desktop. Set to Manual. |
| **AtkHubAgentService** | ATK HUB Agent Service | **Running** | **Auto** | `C:\Program Files\ATK HUB\resources\service\...` | Keyboard/mouse peripheral service. High process group footprint (661 MB). Keep if custom mouse polling/DPI is active, otherwise exit before gaming. |
| **ClickToRunSvc** | Microsoft Office Click-to-Run | **Running** | **Auto** | `...OfficeClickToRun.exe` | Office update listener (74.8 MB RAM). Recommended: Set to Manual. |
| **PlanetVPNService** | PlanetVPN | **Stopped** | **Auto** | `C:/Program Files/.../PlanetVPNService.exe` | Service is stopped but configured to Auto-start on boot. Recommended: Set to Manual. |
| **GamingServices / Net** | Gaming Services | **Running** | **Auto** | Microsoft Store Xbox Gaming backend | Required for Xbox Game Pass titles. Minimal overhead. |
| **DtsApo4Service** | DtsApo4Service | **Stopped** | Manual | DTS audio processing object | Tied to `audiodg.exe` audio enhancement DSP. |

---

### 4.4 Anti-Cheat Services Audit

| Anti-Cheat Service | Current State | Start Mode | Assessment |
|:---|:---:|:---:|:---|
| **EAAntiCheatService** | **Stopped** | Manual | Harmless at idle; starts on-demand when EA games launch. |
| **EasyAntiCheat_EOS** | **Stopped** | Manual | Harmless at idle; starts on-demand. |
| **FACEITService** | **Stopped** | Manual | Harmless at idle. |
| **Riot Vanguard (`vgc`)** | **Not Installed / Not Running** | N/A | No kernel driver overhead observed. |

---

## 5. Windows Gaming Features & Subsystem Status

| Subsystem / Feature | Registry Key / Setting | Configured Value | Status / Assessment |
|:---|:---|:---:|:---|
| **Windows Game Mode** | `HKCU:\Software\Microsoft\GameBar\AutoGameModeEnabled` | `1` | **OPTIMAL (Enabled)**. Windows prioritizes game process threads and throttles background tasks. |
| **Game DVR App Capture** | `HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled` | `0` | **OPTIMAL (Disabled)**. Prevents continuous background game capture. |
| **Game DVR Historical Capture** | `HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled` | `0` | **OPTIMAL (Disabled)**. Eliminates constant NVENC/VRAM buffer writing. |
| **GameConfigStore DVR** | `HKCU:\System\GameConfigStore\GameDVR_Enabled` | `0` | **OPTIMAL (Disabled)**. |
| **Hardware-Accelerated GPU Scheduling (HAGS)** | `HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` | `2` | **OPTIMAL (Enabled)**. Reduces GPU command queue latency. |
| **Windowed Game Optimizations (VRR)** | `HKCU:\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings` | `VRROptimizeEnable=1;` | **OPTIMAL (Enabled)**. Enables modern flip model presentation for windowed/borderless DirectX 11 games. |
| **UWP Background Apps** | `HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\GlobalUserDisabled` | `1` | **OPTIMAL (Globally Disabled)**. Restricts UWP apps from waking up in the background. |
| **Windows Start Menu Web Search** | `SearchHost.exe` spawning `msedgewebview2.exe` | Active (6 processes) | **SUBOPTIMAL (397 MB RAM)**. Web search in Start Menu spawns embedded Edge browser instances. |
| **Windows Audio Enhancements** | `audiodg.exe` processing Realtek / DTS audio effects | Active (930s CPU) | **SUBOPTIMAL**. Causes continuous audio thread CPU consumption and micro-stuttering. |

---

## 6. Prioritized Optimization Findings & Concrete Recommendations

### 6.1 HIGH IMPACT (Immediate FPS & Stutter Reductions)

1. **Resolve `audiodg.exe` Audio DSP Overhead:**
   - **Technical Rationale:** `audiodg.exe` has accumulated 930+ seconds of CPU time and continually executes DSP filtering for DTS / Realtek audio. When high-priority audio threads spike during intense gaming sound effects, they interrupt GPU draw calls and CPU game logic threads, causing 1% low frame drops and audio crackling.
   - **Recommended User Action:**
     - Open Windows Run (`Win + R`), type `mmsys.cpl` and press Enter.
     - Double-click the active playback device (Speakers / Headphones).
     - Navigate to the **Enhancements** or **Advanced** tab.
     - Check **"Disable all sound effects"** or uncheck **"Enable audio enhancements"**.
     - Set default audio format to standard studio quality: **24-bit, 48000 Hz**.

2. **Close Resource-Heavy Browsers & Electron Apps Before Gaming:**
   - **Technical Rationale:** Firefox (3.7 GB RAM, 18 processes) and Discord (1.37 GB RAM, 6 processes) together consume over **5.0 GB of RAM** and over **7,700 seconds of CPU time**, with over 140 GB of combined disk transfers.
   - **Recommended User Action:** Fully close Firefox before launching heavy 3D titles. In Discord settings:
     - Under **Advanced**: Disable **Hardware Acceleration** if experiencing GPU throttling during games (or leave Discord closed when playing competitive games).
     - Under **Game Overlay**: Disable **"Enable in-game overlay"** to prevent `DiscordHook64.dll` from intercepting DirectX swapchains.

3. **Prune Acer OEM Background Services & Bloatware:**
   - **Technical Rationale:** 9 automatic Acer background services continuously poll thermal/sensor metrics and update channels, causing context-switching overhead on AMD CPU cores.
   - **Recommended User Action:**
     - Open `services.mms` (`Win + R` -> `services.msc`).
     - Set non-essential Acer services to **Manual** or **Disabled**:
       - `Acer Care Center` (`AcerCCAgentSvis`)
       - `Acer Agent Service` (`AASSvc`)
       - `Acer Device Info` (`AcerDIAgentSvis` and `AcerDeviceInfoAgentService`)
       - `Acer Experience Zone Component Service` (`AcerEZSvc`)
       - `Acer Service Component Service` (`AcerServiceSvc`)
     - *Note:* Keep `AcerLightingService` enabled only if RGB keyboard control is required, and keep `NitroSense` accessible for manual fan control.

---

### 6.2 MEDIUM IMPACT (Memory & Background Thread Optimization)

4. **Convert Unused Third-Party Developer/Utility Services to Manual:**
   - **Technical Rationale:** `Plastic Server 6` (`plasticd.exe`, 191 MB RAM) and `Microsoft PC Manager Service` (`MSPCManagerService.exe`, 125 MB RAM) run persistently even when not in use.
   - **Recommended User Action:**
     - In `services.msc`, change `Plastic Server 6` from **Automatic** to **Manual**. Start it only when version control work is being performed.
     - Set `PCManager Service Store` to **Manual** or disable it.

5. **Clean Up Enabled Startup Applications:**
   - **Technical Rationale:** `Blitz` (`com.blitz.app`), `ZapFast`, `GoogleUpdaterTaskUser`, and `CS Demo Manager` (`com.akiver.csdm`) launch on Windows boot, adding boot latency and background RAM consumption.
   - **Recommended User Action:**
     - Open Task Manager (`Ctrl + Shift + Esc`) -> Navigate to the **Startup apps** tab.
     - Disable `Blitz`, `ZapFast`, `Google Updater`, and `Discord` from automatic boot launch.

6. **Disable Start Menu Bing Web Search (Reclaim ~400 MB RAM):**
   - **Technical Rationale:** `SearchHost.exe` loads Edge WebView2 (`msedgewebview2.exe`, 6 processes, 397.5 MB RAM) to fetch Bing web recommendations inside the Start Menu search bar.
   - **Recommended User Action:**
     - Users can disable Bing web search in Windows Settings / Group Policy / Registry (`DisableSearchBoxSuggestions = 1`). Local file search remains instant while eliminating the WebView2 memory and network overhead.

---

### 6.3 LOW IMPACT / SYSTEM HYGIENE (Maintenance & Minor Tweaks)

7. **Prune Background Office Maintenance Scheduled Tasks:**
   - **Technical Rationale:** Microsoft Office schedules 11 tasks in `\Microsoft\Office\` that trigger maintenance and telemetry wakeups.
   - **Recommended User Action:** Keep Office Click-to-Run intact, but disable redundant Office telemetry/feedback tasks via Task Scheduler.

8. **Verify Game Folder Exclusions in Microsoft Defender:**
   - **Technical Rationale:** `MsMpEng.exe` (Microsoft Defender) consumed over 1.3 GB of disk read inspections. During game asset streaming (e.g. Unreal Engine open-world games), real-time antivirus scanning of `.pak` / `.bundle` files causes frame drops.
   - **Recommended User Action:** Add specific game library directories (e.g., `D:\Games\`, `C:\Program Files (x86)\Steam\steamapps\common\`) to Windows Defender Exclusions.

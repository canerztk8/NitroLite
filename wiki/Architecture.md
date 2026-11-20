# Architecture

Four projects, split along the one line that matters: what is Windows, what is Linux, and what is neither.

```
NitroLite.Core/                     net10.0 — no Windows-only assembly, no UI framework
├── Hardware/
│   ├── IHardwareController.cs      # Telemetry, fan, mode, MUX, battery limit, lighting
│   ├── DeviceCapabilities.cs       # Everything the probe reports, in one record
│   ├── DeviceIdentity.cs           # Model code, family, BIOS and CPU vendor parsing
│   ├── ModelProfile.cs             # Sensor offsets, RPM ceilings, known-broken methods
│   ├── ProfileCatalog.cs           # Embedded profiles.json and the validated profiles.user.json
│   ├── Profiles/profiles.json      # 32 models: Nitro 5/16/17, Nitro V 15/16, Helios, Triton
│   ├── SensorPlausibility.cs       # Rejects 0 °C, 127 °C and NaN before they reach the UI
│   ├── KeyboardCapability.cs       # Zones and effects from GET_CONNECTED_DEVICE
│   ├── LightingConfig.cs           # Zone colours, effect, brightness, speed
│   ├── OpenRgb/                    # OpenRGB SDK client: detects a server on 127.0.0.1:6742
│   └── Wmi/                        # AcerSensorIds, SensorDiscovery, ReadBackPolicy
├── Lang/                           # de, en, es, hi, id, pt, ru (Turkish is the source text)
├── Loc.cs                          # gettext-style catalogues and the {loc:T} markup extension
├── ThemeCatalog.cs                 # Which palettes exist and which of them are dark
├── UiFontCatalog.cs                # The interface's own font stack
├── FanCurve.cs                     # Curve shape, interpolation and the safety floor
├── FanCurvePresets.cs              # The Balanced, Quiet and Gaming templates
├── ManualFanFloor.cs               # The lowest speed the current temperature allows
├── FanCurveLoop.cs                 # Hysteresis, dwell time, and the handover to the firmware
├── FanStallWatchdog.cs             # A fan that stops turning while the CPU is hot
├── ModePolicy.cs                   # Which modes are valid on AC and on battery
├── SchoolModeService.cs            # One-switch battery saving, with an exact undo record
├── BatteryWearHistory.cs           # Measured wear samples, trend and per-year rate
├── RgbProfileService.cs            # Saved presets; Name is identity, DisplayName is translated
├── ActiveRgbStateStore.cs          # What the keyboard is showing right now, across restarts
├── GameProfileService.cs           # Per-game mode, boost, fan and mouse profile
├── GameLibraryRules.cs             # What counts as a game, and what is a launcher
├── BackupService.cs                # Settings, game and RGB profiles in one JSON file
├── SettingsManager.cs              # Load, normalise, save
├── AtomicFile.cs                   # Write to a temp file, flush, rename
├── Redaction.cs                    # The one redactor the log and the report share
├── UpdateService.cs                # GitHub release check, digest verification, executable swap
├── StartupMode.cs                  # Command line parsing, including --wmi-worker
├── CliCommands.cs                  # --mode, --fan, --profile
└── TelemetryFormatter.cs           # Value formatting and "(estimated)" labels

NitroLite.Windows/                  net10.0-windows — the Windows backends
├── Core/
│   ├── AcerAgentClient.cs          # AASSvc TCP protocol (mode, fan, GPU, RGB, telemetry, events)
│   ├── CareCenterClient.cs         # Acer Care Center WebSocket client (80 % charge limit)
│   ├── AtkMouseService.cs          # ATK / VXE mouse HID protocol
│   ├── CpuPowerService.cs          # Windows power plan CPU boost setting
│   ├── PowerAutomationService.cs   # AC/battery transitions and Eco recovery
│   ├── DebloatService.cs           # Acer background services, autostart
│   ├── SystemToolsService.cs       # Keyboard, Acer service, MUX and Windows repair engine
│   ├── ElevatedStartupService.cs   # Optional Task Scheduler entry, XML-based, kept off the disk
│   ├── DiagnosticsBundle.cs        # Log + settings + report in one folder
│   └── Hardware/
│       ├── AgentHardwareController.cs   # Backend 1: AASSvc and Care Center
│       ├── CapabilityProbe.cs           # Schema + profile + cache, in that order
│       ├── SmbiosProbe.cs               # Read-only SMBIOS queries that never throw
│       ├── HardwareReport.cs            # The text and JSON report users attach to issues
│       └── Wmi/                         # Backend 2: the firmware's own WMI class
│           ├── HybridHardwareController.cs  # Picks a backend per call, reconciles on handover
│           ├── WmiHardwareController.cs     # IHardwareController over the worker pipe
│           ├── AcerWmiExecutor.cs           # The byte-level protocol, in the elevated process
│           ├── AcerWmiProtocol.cs           # Method ids and the (value << 8) | status encoding
│           ├── ReadBackPolicy.cs            # Verify every write; 3 sessions before giving up
│           ├── WmiWorkerLauncher.cs         # Starts the helper, once per session
│           ├── WmiWorkerServer.cs           # CurrentUserOnly pipe, nonce handshake, typed commands
│           ├── WmiWorkerClient.cs           # The parent side of that pipe
│           ├── WmiWorkerValidation.cs       # Bounds the worker re-checks before touching hardware
│           └── JobObjectGuard.cs            # Ties the helper's lifetime to the window
└── Windows*.cs                     # Monitor, battery, storage, debloat, update, Nitro key, autostart

NitroLite.Linux/                    net10.0 — the Linux backends
├── LinuxSysfsHardwareController.cs # IHardwareController over acer-wmi and Linuwu-Sense
├── LinuxHwmon.cs                   # Temperature and fan nodes from hwmon
├── LinuxPlatformProfile.cs         # quiet / balanced / balanced-performance / performance
├── LinuxCapabilityProbe.cs         # What this kernel actually exposes
├── LinuxKeyboardProtocol.cs        # 4-zone RGB through Linuwu-Sense
├── LinuxHardwareReport.cs          # The same report, redacted the same way
├── LinuxNitroKeyListener.cs        # The Nitro key from /dev/input
├── Evdev.cs                        # The evdev reader behind it
└── Sysfs/                          # ISysfsFileSystem, a real one and an in-memory one for tests

NitroLite.Desktop/                  net10.0 and net10.0-windows — the Avalonia front end
├── Program.cs                      # Entry point: worker, CLI, one-shot commands, single instance
├── AppHost.cs                      # The composition root: which backend exists on this platform
├── SingleInstanceGuard.cs          # Mutex + named pipe on Windows, lock file on Linux
├── TrayIconHost.cs                 # Tray menu, parity with the window
├── CliTaskRunner.cs                # --cli and the legacy flags
├── Views/                          # Dashboard, Monitoring, Lighting, Battery, Settings, OSD
├── ViewModels/                     # MainViewModel and the shell labels
├── Controls/                       # SparklineChart, TelemetryChart, WearTrendChart, NitroSlider
├── Themes/                         # Palette loading
└── Assets/Fonts/                   # The eight bundled typefaces and their licences
```

**Data location:** settings, game profiles, RGB profiles, thermal history, battery wear history and the log live in
`%LOCALAPPDATA%\NitroLite` on Windows and `~/.local/share/nitrolite` on Linux. Files that older versions kept next
to the exe are copied there on first launch.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)

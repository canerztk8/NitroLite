# Progress Heartbeat

- Last visited: 2026-11-04T16:07:00Z
- Status: Investigation completed. Writing survey_report.md and handoff.md.
- Key findings gathered:
  * CS2 Valve Trusted Mode DLL signature block of RTSSHooks64.dll causes selectedIndex = -1 in FpsMonitorService.cs, resulting in OSD displaying "-- (RTSS Bekleniyor)".
  * RTSS profile flags cannot bypass Valve Trusted Mode without user risking Trust Factor penalties via -allow_third_party_software.
  * Windows ETW provider Microsoft-Windows-DXGI ({CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}) Event 42 (Present Start) and Event 43 (Present Stop) provide 100% out-of-process, zero-ban-risk frametime telemetry.
  * Intel PresentMon CLI engine (PresentMon-1.10.0-x64.exe and PresentMon-2.3.1-x64.exe) is already present on system in RTSS Plugins and verified fully operational via real-time stdout CSV streaming.
  * 0.1% Low FPS anomaly in Spotifast (e.g. 398798 FPS) is caused by unvalidated dwStatFramerate0Dot1PercentLow at offset 5092, microsecond compositing intervals (<1ms), and lack of clamping against average FPS (0.1% Low must never exceed average FPS).
  * Designed comprehensive hybrid architecture with IFpsTelemetryProvider, RtssSharedMemoryProvider, PresentMonProcessProvider / EtwProvider, and automatic fallback in FpsMonitorService.

# Handoff Report: Low FPS Survey & Architectural Recommendations

**Agent**: `explorer_fps_survey_1` (Teamwork Explorer)  
**Parent Conversation ID**: `e4b46c3d-cb49-4896-97ae-3dbd1b395c38`  
**Working Directory**: `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_fps_survey_1\`  
**Associated Detailed Report**: `survey_report.md`

---

## 1. Observation

1. **`NitroLite/Core/FpsMonitorService.cs` (Lines 292-293 & 323-330)**:
   ```csharp
   uint low1Raw = entryBytes.Length >= 5092 ? BitConverter.ToUInt32(entryBytes, 5088) : 0;
   uint low01Raw = entryBytes.Length >= 5096 ? BitConverter.ToUInt32(entryBytes, 5092) : 0;
   ...
   if (low01Raw > 0)
   {
       low01Fps = low01Raw / 10.0;
   }
   if (low1Raw > 0)
   {
       low1Fps = low1Raw / 10.0;
   }
   ```
   Code attempts to read 1.0% Low and 0.1% Low FPS as 10x fixed-point integers from byte offsets 5088 and 5092.

2. **`RTSSSharedMemory.h` in `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h` (Lines 522-545)**:
   ```cpp
   DWORD   dwStatFrameTimeBuf[1024]; // Offset 924 to 5020 (4096 bytes)
   DWORD   dwStatFrameTimeBufPos;    // Offset 5020
   DWORD   dwStatFrameTimeBufFramerate; // Offset 5024
   LARGE_INTEGER qwAudioCapturePTTEventPush; // Offset 5028
   LARGE_INTEGER qwAudioCapturePTTEventRelease; // Offset 5036
   LARGE_INTEGER qwAudioCapturePTTEventPush2; // Offset 5044
   LARGE_INTEGER qwAudioCapturePTTEventRelease2; // Offset 5052
   DWORD   dwPrerecordSizeLimit; // Offset 5060
   DWORD   dwPrerecordTimeLimit; // Offset 5064
   LARGE_INTEGER qwStatTotalTime; // Offset 5068
   DWORD   dwStatFrameTimeLowBuf[1024]; // Offset 5076 to 9172 (4096 bytes!)
   DWORD   dwStatFramerate1Dot0PercentLow; // Offset ~9172
   DWORD   dwStatFramerate0Dot1PercentLow; // Offset ~9176
   ```
   `dwStatFrameTimeLowBuf[1024]` is an array of 1024 DWORDs (4096 bytes). Real offsets for `dwStatFramerate0Dot1PercentLow` are at ~9176, NOT 5092!
   Offset 5092 is the 4th element of `dwStatFrameTimeLowBuf`, which stores raw frame times in **microseconds**.

3. **`NitroLite.Tests/FpsTelemetryTests.cs` (Lines 194-196)**:
   ```csharp
   // dwStatFramerate0Dot1PercentLow = 950 (95.0 FPS fixed-point)
   BitConverter.GetBytes((uint)950).CopyTo(appEntry, 5092);
   ```
   The unit test hardcoded the same flawed offset 5092, which masked the structural discrepancy.

4. **`NitroLite/OsdOverlayWindow.xaml.cs` (Lines 163-164 & 170-172)**:
   ```csharp
   TxtOsdFps.Text = TelemetryFormatter.FormatFps(fpsInfo.Fps, true);
   TxtOsd01LowFps.Text = TelemetryFormatter.FormatLow01Fps(fpsInfo.Low01Fps, true);
   ...
   bool hasGame = !string.IsNullOrWhiteSpace(fpsInfo.GameTitle);
   TxtOsdFps.Text = hasGame ? "-- (RTSS Bekleniyor)" : "-- FPS";
   ```
   There is no sanity clamping between `Low01Fps` and `Fps`. Furthermore, when CS2 runs, RTSS hook DLL (`RTSSHooks64.dll`) is rejected by Valve Trusted Mode, causing `TxtOsdFps.Text` to be stuck at `-- (RTSS Bekleniyor)`.

5. **Existing Solution Test Suite Execution**:
   Command: `dotnet test NitroLite.sln`
   Result: `Passed! - Failed: 0, Passed: 320, Skipped: 0, Total: 320, Duration: 4 s`.
   Baseline test suite is completely green.

---

## 2. Logic Chain

1. **Spotifast 398798 FPS Derivation**:
   - Spotifast/Spotify runs hardware-accelerated rendering (DirectX/CEF).
   - When no game profile is active, `FpsMonitorService.GetCurrentFps(null, null)` falls back to `lastForegroundApp` or highest `time1`.
   - Spotifast is hooked by RTSS. Because it is a 2D desktop music player, it idles between UI ticks, resulting in single frames taking ~3.987980 seconds ($3,987,980\,\mu s$).
   - RTSS writes $3,987,980\,\mu s$ into `dwStatFrameTimeLowBuf`.
   - `FpsMonitorService` reads offset 5092 (inside `dwStatFrameTimeLowBuf`), obtaining `3987980`.
   - It divides by 10.0: $3,987,980 / 10.0 = 398,798.0\text{ FPS}$.
   - Because `low01Fps > 0`, the ring buffer percentile code at line 333 is bypassed.
   - Without any upper clamping (`low01Fps <= fps`), 398798 FPS is passed to the OSD and displayed.

2. **Percentile Math & Filtering Deficiencies**:
   - In `FpsMonitorService.cs` lines 342-349:
     - `ft > 500`: Allows 500 us (2000 FPS), admitting microsecond noise.
     - `validFrameTimes.Count >= 10`: A sample of 10 frames calculates the 90th percentile (10% Low), not 0.1% Low ($1/1000$).
     - No invariant clamp: 0.1% Low can physically never exceed the Average FPS ($FPS_{0.1\% Low} \le FPS_{avg}$).

3. **CS2 / Anti-Cheat Blocking Mechanism**:
   - `cs2.exe` enforces Valve Trusted Mode, blocking `RTSSHooks64.dll`.
   - RTSS cannot hook `cs2.exe`, creating no app entry in shared memory.
   - To monitor CS2 without requiring players to launch with `-allow_third_party_software` (which risks Trust Factor penalties), an OS-level non-injected trace mechanism (Windows DXGI ETW provider `{CA11C00C-18C3-436A-B4C3-BBE54F002EC3}` or PresentMon) is needed as a fallback.

---

## 3. Caveats

- In pure read-only mode, no code files have been modified.
- Full details of ETW C# implementation vs bundled PresentMon wrapper are documented in `survey_report.md`. Both options operate with Administrator rights without injecting code into CS2.
- The exact offset for `dwStatFramerate0Dot1PercentLow` in RTSS 2.13+ is ~9176, but RTSS only computes it during active benchmark recording (`STATFLAG_RECORD`). For live OSD display, calculating from the 1024-entry frametime ring buffer (`dwStatFrameTimeBuf` at offset 924) is the strictly correct and recommended approach.

---

## 4. Conclusion

1. The "398798 FPS" bug is caused by reading a microsecond frametime ($3,987,980\,\mu s$) from offset 5092 (which is inside `dwStatFrameTimeLowBuf[1024]`, not the 0.1% low stat field), dividing by 10, and failing to clamp against average FPS.
2. The fix requires:
   - Deprecating/removing raw offset 5092/5088 reads and computing live 0.1% Low from `dwStatFrameTimeBuf[1024]` (offset 924).
   - Filtering microsecond noise ($< 1000\,\mu s$) and idle frames ($> 500,000\,\mu s$).
   - Enforcing minimum sample size ($N \ge 30$) and mathematical clamping: $\text{Low01Fps} \le \text{Low1Fps} \le \text{Fps}$.
   - Adding a safety ceiling ($> 2000\text{ FPS} \implies \text{"-- FPS"}$) in `TelemetryFormatter.cs`.
   - Implementing a non-injected ETW / PresentMon fallback engine for CS2.

---

## 5. Verification Method

To verify the findings and subsequent fixes:
1. **Run Baseline Test Suite**:
   ```powershell
   dotnet test NitroLite.sln
   ```
   Must pass all 320 tests.
2. **Review Detailed Survey Report**:
   Inspect `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\explorer_fps_survey_1\survey_report.md`.
3. **Verify Struct Offsets**:
   Inspect `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h` lines 522-545 to confirm `dwStatFrameTimeLowBuf[1024]` spans 4096 bytes starting at offset 5076/5080.
4. **Invalidation Conditions**:
   The diagnosis would be invalidated if RTSS shared memory did not have `dwStatFrameTimeLowBuf[1024]` (4096 bytes) between offset 5076 and 9172. As verified directly from the installed SDK header, the 4096-byte array is unequivocally present.

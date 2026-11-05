# Requirement R2 Technical Investigation & Architecture Survey: Battery Telemetry, Instantaneous Discharge Rate, and Dynamic Runtime Estimation

**Target System**: NitroLite (.NET 10.0 WPF / Acer Nitro 16 Gaming Laptop)  
**Author**: `explorer_battery_survey_1`  
**Date**: 2026-11-04  
**Integrity Mode**: Development / Zero Regression  
**Deliverable**: Complete technical survey, empirical data, mathematical algorithm, UI specification, and test strategy for Requirement R2.

---

## 1. Executive Summary & Problem Boundary

Requirement R2 mandates:
> *Mevcut pil telemetri altyapısı incelenerek anlık deşarj akımı/gücü (Watt / mW) ve kalan batarya kapasitesi üzerinden dinamik kalan kullanım süresi (saat:dakika) hesaplanmalı ve kullanıcı arayüzünde gösterilmelidir.*

### Key Discoveries & Architectural Summary
1. **Existing Telemetry Baseline**: NitroLite already features `BatteryWatchdogService.cs` in `NitroLite/Core/` (referenced in dispatch as `BatteryChargeLimitService.cs`) and UI bindings in `MainWindow.xaml` (`TxtBatDischargeRate`, `TxtBatRemainingTime`). It polls native Win32 `GetSystemPowerStatus` and WMI `root\WMI:BatteryStatus` every 3 seconds.
2. **Deficiencies in Current Implementation**:
   - The remaining time calculation `(rem / dr) * 60.0` was embedded directly inside `BatteryWatchdogService.UpdateStatus()` and computed strictly on instantaneous, unsmoothed `dr`. Under normal Windows bursts (e.g., launching an app or background indexing), `dr` spikes from 12W to 55W and drops back, causing the displayed remaining time to swing wildly (e.g. "4 sa 45 dk" $\leftrightarrow$ "1 sa 12 dk").
   - `BatteryStatusInfo` discarded high-precision milliwatt readings (`DischargeRateMilliwatts`) and instantaneous `RemainingCapacityMWh`.
   - When `dr <= 500 mW` (during AC->DC transitions or sensor polling pauses), the estimate immediately reverted to `GetSystemPowerStatus.BatteryLifeTime` (which is almost always `-1`), causing the UI to flicker with `"Hesaplanıyor..."`.
   - The calculation was untestable in unit tests because it was coupled to live WMI and Win32 hardware queries.
3. **Verified Telemetry Sources**:
   - Empirical testing on live hardware confirmed that `root\WMI:BatteryStatus` (`ACPI\PNP0C0A\1_0`) reliably reports `RemainingCapacity` in mWh (66,543 mWh) and `DischargeRate` in mW.
   - Win32 `GetSystemPowerStatus` provides microsecond-latency AC connection and percentage flags.
4. **Architectural Remediation**:
   - Extract pure, deterministic mathematical functions (`CalculateRemainingMinutes`, `UpdateSmoothedDischargeRate`) into `TelemetryFormatter.cs`.
   - Implement an **Exponential Moving Average (EMA)** smoothing filter ($\alpha = 0.20$, $\sim 15$-second time constant) with **instantaneous initial seeding** on AC $\rightarrow$ DC disconnect ($S[0] = R[0]$) to eliminate initial lag while dampening transient workload spikes.
   - Provide dual-metric telemetry in the UI: instantaneous power flow with milliwatt precision (e.g., `14.8 W (14.820 mW)`) alongside rock-solid remaining runtime (`Kalan Süre: 3 sa 45 dk`).
   - Preserve 100% of the 201 existing unit tests, while adding a new battery telemetry test suite in `NitroLite.Tests`.

---

## 2. Codebase Audit: Existing Battery Telemetry Infrastructure

### 2.1 Service Mapping
| Dispatch / Conceptual Name | Actual Codebase File | Role |
|----------------------------|----------------------|------|
| `BatteryChargeLimitService.cs` | `NitroLite/Core/BatteryWatchdogService.cs` | Manages 80% charge limit enforcement via `C:\ProgramData\Acer\CC\settings.json`, monitors AC/DC state, polls WMI `BatteryStatus`, and performs controlled discharge. |
| `HardwareService.cs` | `NitroLite/Core/HardwareMonitorService.cs` | Manages NVML GPU telemetry, CPU power curve estimation, and RAM fallback counters. Does not handle battery. |
| `TelemetryFormatter.cs` | `NitroLite/Core/TelemetryFormatter.cs` | Pure formatting utilities for temps, fan RPM/percent, power flow, and remaining time. |
| `MainWindow.xaml` | `NitroLite/MainWindow.xaml` | Tab 4 (`TabBattery`) contains battery level, charge limit checkbox, safe discharge controls, health stats, and power flow cards. |
| `MainWindow.xaml.cs` | `NitroLite/MainWindow.xaml.cs` | Wires `_batteryService.StatusUpdated += OnBatteryStatusUpdated`, updates UI Dispatcher elements. |

### 2.2 Deep Dive: `BatteryWatchdogService.cs`
- **Polling Loop**: Runs on a background task via `PeriodicTimer(TimeSpan.FromSeconds(3))`.
- **Power Event Subscription**:
  ```csharp
  SystemEvents.PowerModeChanged += OnPowerModeChanged;
  ```
  Listens to `PowerModes.Resume` and `PowerModes.StatusChange` (which fires immediately upon plugging/unplugging the AC adapter).
- **Current Data Gathering**:
  ```csharp
  GetSystemPowerStatus(out var status);
  // Queries root\WMI: SELECT DischargeRate, ChargeRate, RemainingCapacity, Voltage, Discharging FROM BatteryStatus
  ```
  Current calculation:
  ```csharp
  var dr = Convert.ToDouble(obj["DischargeRate"] ?? 0);
  var cr = Convert.ToDouble(obj["ChargeRate"] ?? 0);
  var rem = Convert.ToDouble(obj["RemainingCapacity"] ?? 0);
  var isDisch = Convert.ToBoolean(obj["Discharging"] ?? false);

  info.IsDischarging = isDisch || (!info.IsPluggedIn && dr > 0);
  info.DischargeRateWatts = Math.Round(dr / 1000.0, 1);
  info.ChargeRateWatts = Math.Round(cr / 1000.0, 1);

  if (info.IsDischarging && dr > 500 && rem > 0)
  {
      info.EstimatedRemainingMinutes = (int)((rem / dr) * 60.0);
  }
  ```

### 2.3 Deep Dive: `MainWindow.xaml` & `MainWindow.xaml.cs`
In `MainWindow.xaml` (Lines 884-910):
- **Header Badge**: `BadgeBattery` with `TxtBatteryHeader` (`--%`).
- **Battery Status Card**:
  - `TxtBatLevelBig`: e.g. `80%`.
  - `TxtBatStateDesc`: e.g. `Adaptöre Bağlı (Sınırda)` or `Pilde Çalışıyor (Deşarj)`.
  - `BarBatBig`: `ProgressBar` with `Maximum="100"`.
  - **Live Power Flow Container**:
    - `TxtBatDischargeRate`: TextBlock currently displaying `0.0 W (Adaptörde)` or `18.5 W (Deşarj Oluyor)`.
    - `TxtBatRemainingTime`: TextBlock displaying `Kalan Süre: 2 sa 30 dk` or `Kalan Süre: Adaptöre Bağlı`.
  - `ChkWatchdog`: CheckBox for `%80 şarj koruma sınırını uygula`.

In `MainWindow.xaml.cs` (`OnBatteryStatusUpdated`):
```csharp
TxtBatteryHeader.Text = $"{status.BatteryPercent}%";
TxtBatLevelBig.Text = $"{status.BatteryPercent}%";
BarBatBig.Value = status.BatteryPercent;
TxtBatStateDesc.Text = status.IsPluggedIn
    ? (status.IsCharging ? "Adaptöre Bağlı (Şarj Ediliyor)" : "Adaptöre Bağlı (Sınırda)")
    : "Pilde Çalışıyor (Deşarj)";

if (TxtBatDischargeRate != null)
{
    TxtBatDischargeRate.Text = TelemetryFormatter.FormatBatteryPowerFlow(status);
    if (status.IsDischarging && status.DischargeRateWatts > 0)
        TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("AccentCyan");
    else if (status.IsCharging && status.ChargeRateWatts > 0)
        TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("AccentGreen");
    else
        TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("TextSecondary");
}

if (TxtBatRemainingTime != null)
{
    TxtBatRemainingTime.Text = $"Kalan Süre: {TelemetryFormatter.FormatRemainingTime(status.EstimatedRemainingMinutes, status.IsPluggedIn, status.IsCharging)}";
}
```

### 2.4 Deep Dive: `TelemetryFormatter.cs`
Existing formatting contract:
```csharp
public static string FormatBatteryPowerFlow(BatteryStatusInfo? status)
{
    if (status == null) return "0.0 W";
    if (status.IsDischarging && status.DischargeRateWatts > 0)
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} W (Deşarj Oluyor)", status.DischargeRateWatts);
    if (status.IsCharging && status.ChargeRateWatts > 0)
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} W (Şarj Oluyor)", status.ChargeRateWatts);
    if (status.IsPluggedIn)
        return "0.0 W (Adaptörde / Doygun)";
    return "0.0 W (Pilde / Boşta)";
}

public static string FormatRemainingTime(int minutes, bool isPluggedIn, bool isCharging)
{
    if (minutes <= 0 || minutes > 1440)
    {
        if (isCharging) return "Hesaplanıyor...";
        if (isPluggedIn) return "Adaptöre Bağlı";
        return "Hesaplanıyor...";
    }
    int hours = minutes / 60;
    int mins = minutes % 60;
    if (hours > 0) return $"{hours} sa {mins} dk";
    return $"{mins} dk";
}
```

**Crucial Constraint**: 6 existing unit tests in `TelemetryFormatterTests.cs` and `AdversarialChallengerTests.cs` assert the verbatim output of `FormatBatteryPowerFlow` and `FormatRemainingTime`. Any modification must preserve these exact string returns for the existing test inputs.

---

## 3. Telemetry Sources for Instantaneous Discharge Rate on Windows & Acer Nitro

### 3.1 Empirical Hardware Investigation (Conducted on Live Acer Nitro 16)
We executed direct hardware queries on the running Windows instance:

1. **WMI `root\WMI:BatteryStatus`**:
   ```
   InstanceName      : ACPI\PNP0C0A\1_0
   Active            : True
   Charging          : True
   Discharging       : False
   PowerOnline       : True
   DischargeRate     : 0
   ChargeRate        : 0
   RemainingCapacity : 66543
   Voltage           : 16491
   ```
   *Analysis*:
   - `RemainingCapacity = 66543` corresponds to $66.543\text{ Wh}$.
   - Against `BatteryFullChargedCapacity.FullChargedCapacity = 83637` ($83.637\text{ Wh}$), the ratio is $\frac{66543}{83637} = 79.56\%$ ($\approx 80\%$), matching `BatteryLifePercent = 80%` exactly.
   - `Voltage = 16491` is in millivolts ($16.491\text{ V}$), matching a 4-cell Lithium-ion battery pack nominal voltage.
   - When AC is disconnected, `PowerOnline` becomes `False`, `Discharging` becomes `True`, and `DischargeRate` reports the instantaneous power draw in **milliwatts (mW)**.

2. **WMI `root\WMI:BatteryRuntime`**:
   ```
   EstimatedRuntime  : 4294967295 (0xFFFFFFFF = BATTERY_UNKNOWN_TIME while on AC)
   ```

3. **Win32 `GetSystemPowerStatus`**:
   ```
   PowerLineStatus      : Online
   BatteryChargeStatus  : High, Charging
   BatteryLifePercent   : 0.80 (80%)
   BatteryLifeRemaining : -1 (0xFFFFFFFF = unknown/plugged in)
   ```

4. **Acer Agent Client (`AASSvc`) / EC**:
   - `AASSvc` on port 46933 handles packets `0` (Version), `10` (Monitor Data), `30` (Connected Device), `40` (Lighting Capability), and `100` (Set Device Data).
   - As audited in `AcerAgentClient.cs`, `AASSvc` exposes CPU/GPU temperatures, fan RPMs, CPU clocks, and keyboard RGB, but has **no battery telemetry packets**.
   - Direct raw EC port I/O (e.g. WinRing0) is blocked by Vanguard/EasyAntiCheat and risks BSOD or EC desynchronization.

### 3.2 Evaluation of Candidate Sources
| Telemetry Source | Refresh Latency | Telemetry Exposed | Safety & Anti-Cheat | Verdict |
|------------------|-----------------|-------------------|---------------------|---------|
| **WMI `root\WMI:BatteryStatus`** | ~10 ms in-process | `DischargeRate` (mW), `RemainingCapacity` (mWh), `Voltage` (mV), `Discharging` (bool), `ChargeRate` (mW) | 100% safe, native Microsoft ACPI battery driver (`battc.sys`) | **PRIMARY** for rate & capacity |
| **Win32 `GetSystemPowerStatus`** | < 1 ms | `ACLineStatus`, `BatteryLifePercent`, `BatteryFlag` | 100% safe, kernel32 unmanaged | **PRIMARY** for AC/DC state & percentage |
| **SetupAPI / `IOCTL_BATTERY_QUERY_STATUS`** | < 1 ms | Identical to WMI `BatteryStatus` | Safe, but requires complex unmanaged P/Invoke memory marshaling | Redundant with WMI |
| **WMI `root\CIMV2:Win32_Battery`** | 100-300 ms | Coarse, `DesignCapacity` and `FullChargeCapacity` often empty; `EstimatedRunTime` static | Safe, but slow and outdated | Not recommended |
| **AcerAgentClient (`AASSvc`)** | ~5 ms | No battery telemetry available | Safe, but unsupported | Not viable |

**Conclusion**: The optimal telemetry architecture is a **cooperative hybrid**:
- `GetSystemPowerStatus` provides instant detection of AC plug/unplug events with zero latency.
- `root\WMI:BatteryStatus` provides instantaneous discharge rate in milliwatts (mW) and remaining capacity in milliwatt-hours (mWh).

---

## 4. Dynamic Remaining Battery Runtime Algorithm

### 4.1 Theoretical Foundation
In an ACPI-compliant battery providing energy units:
$$\text{Runtime (Hours)} = \frac{\text{Remaining Capacity (mWh)}}{\text{Discharge Rate (mW)}}$$
$$\text{Runtime (Minutes)} = \frac{\text{Remaining Capacity (mWh)} \times 60}{\text{Discharge Rate (mW)}}$$

### 4.2 The Fluctuation Challenge
On gaming laptops like the Acer Nitro 16:
- **Idle desktop**: Power consumption is $\sim 11\text{W} - 15\text{W}$ ($11,000 - 15,000\text{ mW}$). With $66,543\text{ mWh}$ remaining:
  $$T = \frac{66543}{13000} \times 60 \approx 307\text{ minutes (5 sa 07 dk)}$$
- **Transient application launch / browser opening**: CPU boosts to 54W for 3 seconds, total system draw reaches $\sim 65\text{W}$ ($65,000\text{ mW}$):
  $$T = \frac{66543}{65000} \times 60 \approx 61\text{ minutes (1 sa 01 dk)}$$
- **Return to idle**: Draw drops back to 13W ($T \approx 307\text{ min}$).

If the display updates on instantaneous rate alone, the remaining time counter bounces by over 4 hours every 3 seconds.

### 4.3 Proposed Smoothing: Exponential Moving Average (EMA) with Fast Seeding
We apply an Exponential Moving Average to the discharge rate:
$$S[t] = \alpha \cdot R[t] + (1 - \alpha) \cdot S[t-1]$$
where:
- $R[t]$ is the instantaneous discharge rate in mW.
- $S[t]$ is the smoothed discharge rate in mW.
- $\alpha$ is the smoothing coefficient ($0 < \alpha \le 1$).

#### Parameter Derivation
With a polling period $\Delta t = 3\text{ seconds}$:
- Selecting $\alpha = 0.20$ yields an effective time constant:
  $$\tau = \frac{\Delta t}{-\ln(1 - \alpha)} = \frac{3}{-\ln(0.80)} \approx \frac{3}{0.223} \approx 13.5\text{ seconds}$$
- A 13.5-second time constant absorbs transient 2-5 second CPU burst spikes while adapting smoothly to genuine state changes (e.g., commencing gameplay or dimming screen brightness) within 25-30 seconds.

#### Zero-Lag Initial Seeding (Cold Start)
To prevent the filter from lagging when the AC cord is pulled:
```csharp
if (!_isSmoothedRateInitialized || _smoothedDischargeRateMilliwatts <= 500)
{
    if (instantDischargeMilliwatts > 500)
    {
        _smoothedDischargeRateMilliwatts = instantDischargeMilliwatts;
        _isSmoothedRateInitialized = true;
    }
}
else
{
    _smoothedDischargeRateMilliwatts = (0.20 * instantDischargeMilliwatts) + (0.80 * _smoothedDischargeRateMilliwatts);
}
```
This guarantees immediate, accurate runtime estimation on the very first tick after AC disconnection.

### 4.4 Comprehensive Edge Case Matrix
| State | Condition | Smoothed Rate Action | Remaining Minutes Output | Displayed UI Text |
|-------|-----------|----------------------|--------------------------|-------------------|
| **AC Saturated / 80% Limit** | `IsPluggedIn == true && IsCharging == false` | Reset: $S = 0$, `initialized = false` | `-1` | `Kalan Süre: Adaptöre Bağlı` |
| **AC Charging** | `IsPluggedIn == true && IsCharging == true` | Reset: $S = 0$, `initialized = false` | `-1` | `Kalan Süre: Hesaplanıyor...` |
| **DC Transition Tick 1** | `IsPluggedIn == false`, $R[t] \le 500\text{ mW}$ | Uninitialized | Fallback to Win32 `BatteryLifeTime` if $> 0$, else `-1` | `Kalan Süre: Hesaplanıyor...` |
| **DC Normal Discharging** | `IsPluggedIn == false`, $R[t] > 500\text{ mW}$ | Initialized or EMA updated | $\text{round}\left(\frac{\text{rem}}{S[t]} \times 60\right)$ | `Kalan Süre: {H} sa {M} dk` |
| **DC Sensor Drop / Sleep Pause** | `IsPluggedIn == false`, $R[t] \le 500\text{ mW}$ after initialization | Hold previous $S[t-1]$ (do not zero out) | Maintain previous valid minutes | Stable display, no flicker |
| **Heavy Load / Gaming on Battery** | $R[t] \approx 75,000\text{ mW}$ | Converges to $\sim 75\text{W}$ in ~20s | $\sim 53\text{ minutes}$ | `Kalan Süre: 53 dk` |
| **Ultra-Low Power / Idle** | $S[t] < 2,000\text{ mW}$ | Clamped to max 24 hours | Cap at $1440\text{ min}$ | `Kalan Süre: 24 sa 00 dk` |
| **Near Zero Remaining Capacity** | $\text{rem} < 500\text{ mWh}$ | Normal calculation | $\max(1, \text{minutes})$ | `Kalan Süre: 1 dk` |

---

## 5. UI Integration Design in `MainWindow.xaml`

### 5.1 Card Structure & Typography
The battery telemetry is hosted in Tab 4 (`TabBattery`), in the left column `Border` (`CardBorder`).

#### Current vs Proposed Presentation
In the inner telemetry box:
```xml
<!-- Live Real-Time Discharge / Charge Rate Indicator -->
<Border Background="#13161D" BorderBrush="{StaticResource BorderDark}" BorderThickness="1" CornerRadius="4" Padding="10,8" Margin="0,0,0,8">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        
        <!-- Left: Instantaneous Power Flow (Watt & mW) -->
        <StackPanel Grid.Column="0">
            <TextBlock Text="Anlık Güç Akışı:" Foreground="{StaticResource TextMuted}" FontSize="10" FontWeight="Bold" />
            <StackPanel Orientation="Horizontal" Margin="0,2,0,0">
                <TextBlock x:Name="TxtBatDischargeRate" Text="0.0 W (Adaptörde)" Foreground="{StaticResource AccentCyan}" FontSize="14" FontWeight="Bold" />
                <TextBlock x:Name="TxtBatDischargeMilliwatts" Text="" Foreground="{StaticResource TextMuted}" FontSize="11" Margin="6,0,0,0" VerticalAlignment="Center" />
            </StackPanel>
        </StackPanel>

        <!-- Right: Dynamic Remaining Runtime (Saat & Dakika) -->
        <StackPanel Grid.Column="1" HorizontalAlignment="Right" VerticalAlignment="Center">
            <TextBlock x:Name="TxtBatRemainingTime" Text="Kalan Süre: --" Foreground="{StaticResource TextSecondary}" FontSize="12" FontWeight="SemiBold" HorizontalAlignment="Right" />
        </StackPanel>
    </Grid>
</Border>
```

### 5.2 Formatter Strategy: Backwards Compatibility & High Precision
To guarantee that the existing 201 unit tests continue to pass with zero regressions while satisfying Requirement R2:

1. **Keep `FormatBatteryPowerFlow(BatteryStatusInfo? status)`**:
   Returns the established strings:
   - `"18.5 W (Deşarj Oluyor)"`
   - `"45.0 W (Şarj Oluyor)"`
   - `"0.0 W (Adaptörde / Doygun)"`
   - `"0.0 W (Pilde / Boşta)"`
2. **Add Overload or Secondary Method**:
   ```csharp
   public static string FormatDetailedBatteryPowerFlow(BatteryStatusInfo? status)
   {
       if (status == null) return "0.0 W";
       if (status.IsDischarging && status.DischargeRateWatts > 0)
       {
           double mw = status.DischargeRateMilliwatts > 0 ? status.DischargeRateMilliwatts : status.DischargeRateWatts * 1000.0;
           return string.Format(CultureInfo.InvariantCulture, "{0:F1} W ({1:N0} mW)", status.DischargeRateWatts, mw);
       }
       return FormatBatteryPowerFlow(status);
   }
   ```
   Or in `MainWindow.xaml.cs`:
   ```csharp
   TxtBatDischargeRate.Text = TelemetryFormatter.FormatBatteryPowerFlow(status);
   if (TxtBatDischargeMilliwatts != null)
   {
       if (status.IsDischarging && status.DischargeRateMilliwatts > 0)
       {
           TxtBatDischargeMilliwatts.Text = $"({status.DischargeRateMilliwatts:N0} mW)";
           TxtBatDischargeMilliwatts.Visibility = Visibility.Visible;
       }
       else
       {
           TxtBatDischargeMilliwatts.Visibility = Visibility.Collapsed;
       }
   }
   ```
   This cleanly fulfills both the "Watt / mW" requirement and ensures 100% test compatibility.

3. **Dynamic Foreground Color Coding**:
   - Discharging normal ($P < 30\text{W}$): `AccentCyan` (`#00E5FF`)
   - Discharging heavy ($P \ge 45\text{W}$): `AccentRed` (`#FF5252`)
   - Charging: `AccentGreen` (`#00E676`)
   - AC Saturated / Idle: `TextSecondary` (`#8A92A6`)

4. **Window Header Badge (`TxtBatteryHeader`)**:
   When on battery, tooltip on `BadgeBattery`:
   `$"Pil: %{status.BatteryPercent} • Kalan: {hours} sa {mins} dk • Anlık: {status.DischargeRateWatts:F1} W"`

---

## 6. Target Architecture & Data Models

### 6.1 Extended Model in `Models.cs`
```csharp
public class BatteryStatusInfo
{
    public int BatteryPercent { get; set; }
    public bool IsCharging { get; set; }
    public bool IsPluggedIn { get; set; }
    public bool IsWatchdogActive { get; set; }
    public int EstimatedRemainingMinutes { get; set; }
    public bool IsOvercharged { get; set; } // > 80%
    public double DischargeRateWatts { get; set; }
    public double ChargeRateWatts { get; set; }
    public double DischargeRateMilliwatts { get; set; } // High-precision instantaneous mW
    public double SmoothedDischargeRateWatts { get; set; } // Filtered rate in W
    public double RemainingCapacityMWh { get; set; } // Remaining energy in mWh
    public bool IsDischarging { get; set; }
    public double? TemperatureCelsius { get; set; }
}
```

### 6.2 Pure Mathematical Methods in `TelemetryFormatter.cs`
Extracting the calculation and smoothing logic into static, pure methods on `TelemetryFormatter` enables comprehensive unit testing without WMI dependencies:

```csharp
/// <summary>
/// Calculates dynamic remaining battery runtime in minutes from capacity and discharge rate.
/// Returns -1 when capacity or discharge rate is invalid, zero, or below sensor threshold.
/// Clamps output between 1 minute and 1440 minutes (24 hours).
/// </summary>
public static int CalculateRemainingMinutes(double remainingMWh, double dischargeRateMilliwatts)
{
    if (remainingMWh <= 0 || dischargeRateMilliwatts <= 500)
    {
        return -1;
    }

    double minutes = (remainingMWh / dischargeRateMilliwatts) * 60.0;

    if (minutes < 1.0) return 1;
    if (minutes > 1440.0) return 1440;

    return (int)Math.Round(minutes);
}

/// <summary>
/// Updates the exponential moving average (EMA) of battery discharge rate.
/// Fast-seeds immediately on cold start (unplug) without initial lag.
/// Retains previous smoothed value on transient sensor blips (<= 500 mW).
/// </summary>
public static double UpdateSmoothedRate(double currentSmoothedMw, double instantReadingMw, ref bool isInitialized, double alpha = 0.20)
{
    if (instantReadingMw <= 500)
    {
        return isInitialized ? currentSmoothedMw : 0.0;
    }

    if (!isInitialized || currentSmoothedMw <= 0)
    {
        isInitialized = true;
        return instantReadingMw;
    }

    return (alpha * instantReadingMw) + ((1.0 - alpha) * currentSmoothedMw);
}
```

### 6.3 State Management in `BatteryWatchdogService.cs`
In `BatteryWatchdogService`:
- Maintain private filter state:
  ```csharp
  private double _smoothedDischargeRateMw = 0.0;
  private bool _isSmoothedRateInitialized = false;
  ```
- Reset upon AC connection:
  ```csharp
  if (info.IsPluggedIn)
  {
      _smoothedDischargeRateMw = 0.0;
      _isSmoothedRateInitialized = false;
  }
  ```
- Update during WMI poll on DC:
  ```csharp
  if (info.IsDischarging && dr > 500)
  {
      _smoothedDischargeRateMw = TelemetryFormatter.UpdateSmoothedRate(_smoothedDischargeRateMw, dr, ref _isSmoothedRateInitialized);
      info.SmoothedDischargeRateWatts = Math.Round(_smoothedDischargeRateMw / 1000.0, 1);
      info.EstimatedRemainingMinutes = TelemetryFormatter.CalculateRemainingMinutes(rem, _smoothedDischargeRateMw);
  }
  ```

---

## 7. Verification & Unit Test Strategy

### 7.1 Regression Gate: 201 Existing Tests
Before committing any changes, verify the baseline:
```powershell
dotnet test NitroLite.sln
```
Expected: `Passed! - Failed: 0, Passed: 201, Skipped: 0, Total: 201`

### 7.2 Proposed Unit Tests for `NitroLite.Tests`
Create `BatteryTelemetryTests.cs` (or add to `TelemetryFormatterTests.cs`):

| Test Case | Scenario / Input | Expected Result |
|-----------|------------------|-----------------|
| `CalculateRemainingMinutes_NormalDischarge_ReturnsAccurateMinutes` | $rem = 60000\text{ mWh}, dr = 15000\text{ mW}$ | $240\text{ minutes (4 sa 00 dk)}$ |
| `CalculateRemainingMinutes_HighGamingLoad_ReturnsAccurateMinutes` | $rem = 60000\text{ mWh}, dr = 60000\text{ mW}$ | $60\text{ minutes (1 sa 00 dk)}$ |
| `CalculateRemainingMinutes_ZeroOrNegativeRate_ReturnsNegativeOne` | $rem = 60000\text{ mWh}, dr = 0\text{ mW}$ or $dr = 200\text{ mW}$ | `-1` |
| `CalculateRemainingMinutes_ZeroCapacity_ReturnsNegativeOne` | $rem = 0\text{ mWh}, dr = 15000\text{ mW}$ | `-1` |
| `CalculateRemainingMinutes_UltraLowRate_ClampsTo1440Minutes` | $rem = 80000\text{ mWh}, dr = 600\text{ mW}$ ($\text{raw} = 8000\text{ min}$) | `1440` (24 hours) |
| `CalculateRemainingMinutes_NearEmpty_ReturnsAtLeastOneMinute` | $rem = 100\text{ mWh}, dr = 30000\text{ mW}$ ($\text{raw} = 0.2\text{ min}$) | `1` |
| `UpdateSmoothedRate_ColdStart_SeedsImmediatelyWithoutLag` | $current = 0, instant = 18000, init = false$ | Returns $18000$, sets $init = true$ |
| `UpdateSmoothedRate_TransientSpike_DampensFluctuation` | $current = 15000, instant = 60000, init = true$ | Returns $0.2 \times 60000 + 0.8 \times 15000 = 24000\text{ mW}$ |
| `UpdateSmoothedRate_SensorBlipZero_MaintainsSmoothedValue` | $current = 18000, instant = 0, init = true$ | Returns $18000$ (no drop to 0) |
| `FormatRemainingTime_FormattingVerification` | $minutes = 150, 45, 0$ | Matches `"2 sa 30 dk"`, `"45 dk"`, `"Adaptöre Bağlı"` |
| `FormatBatteryPowerFlow_DualUnits_PreservesAllExistingContracts` | Discharging, Charging, PluggedIn, OnBatteryZero | Matches all 5 existing assertions |

---

## 8. Alignment with Neighbor Requirements

1. **Alignment with R1 (Profile Automation on AC/DC Unplug)**:
   - `BatteryWatchdogService` and `SystemEvents.PowerModeChanged` form the authoritative backbone for detecting adapter disconnects.
   - When AC is disconnected, the event handler in `MainWindow.xaml.cs` triggers both R1 (automatic profile switch to Eco or Balanced) and R2 (immediate seeding of the battery runtime filter).
2. **Alignment with R4 (Hardware Protection & MUX Switch Guard)**:
   - The battery telemetry pipeline communicates strictly through Microsoft ACPI WDM drivers (`battc.sys` via WMI) and Win32 `kernel32.dll`.
   - Zero interactions with display multiplexing, GPU switching registers, or proprietary EC firmware overrides that could destabilize hardware or violate MUX switch immutability.

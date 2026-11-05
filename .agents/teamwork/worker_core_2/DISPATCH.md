## 2026-11-04T03:29:53Z
You are worker_core_2, a teamwork_worker subagent.

## Working Directory
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_2`

## Authoritative Request & Specifications
Read and follow:
1. `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\ORIGINAL_REQUEST.md` (MUST read first)
2. `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\PROJECT.md`
3. Adversarial findings from challenger_nitrolite_2:
   - `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\challenger_nitrolite_2\handoff.md`
   - `c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\challenger_nitrolite_2\challenge.md`

## MANDATORY INTEGRITY WARNING
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

## Objective: Apply the 2 targeted fixes identified by challenger_nitrolite_2

### Fix 1: Fix Brightness Slider 0 Persistence in `NitroLite/MainWindow.xaml.cs`
In `SliderBrightness_ValueChanged` (around line 1809):
When `val == 0`, before returning, ensure that:
1. `_lighting.Brightness = 0;`
2. `_appSettings.IsBacklightOff = true;`
3. `_appSettings.KeyboardLighting = _lighting;`
4. `SaveSettings();` is called so that `settings.json` is updated immediately with `IsBacklightOff: true` and `Brightness: 0`.
5. `SaveActiveRgbState();` is called.
6. `await _agentClient.TurnOffLightingAsync();` is dispatched.

### Fix 2: Win32 Mutex Abandonment Recovery in `NitroLite/App.xaml.cs`
In `App.xaml.cs` (lines 30–56):
When `!createdNew`, attempt `_mutex?.WaitOne(0)` inside a try/catch block catching `AbandonedMutexException` to acquire primary ownership if an abandoned mutex exists:
```csharp
if (!createdNew)
{
    try
    {
        if (_mutex != null && _mutex.WaitOne(0))
        {
            createdNew = true;
        }
    }
    catch (AbandonedMutexException)
    {
        createdNew = true;
    }
}
```
If `createdNew` is still false, then execute the secondary instance wake logic:
- Win32 `AllowSetForegroundWindow(ASFW_ANY);`
- `wakeEvent.Set();`
- `Environment.Exit(0);`

### Verification
Run using run_command:
1. `dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln`
2. `dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj`
3. Verify that all 197+ tests pass cleanly with 0 failures and 0 warnings.

## Outputs
Write your changes to:
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_2\changes.md`
and write your structured handoff to:
`c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_core_2\handoff.md`.
Send a completion message when finished.

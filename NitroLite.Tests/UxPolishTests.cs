using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NitroLite.Core;
using NitroLite.Tests.Scenarios;
using Xunit;
using WpfButton = System.Windows.Controls.Button;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace NitroLite.Tests;

/// <summary>
/// The four daily-use polish items: the clickable left-rail rows and MOD badge, the collapsible temperature strip,
/// the apply-all-zones button, and the in-button feedback that replaced a toast the user had already missed.
/// </summary>
/// <remarks>
/// These drive a real <see cref="MainWindow"/> built the way the chaos suite builds one — no XAML, so the controls
/// the window would have loaded are either absent (and the code under test has to tolerate that) or supplied by the
/// test. Everything runs on one STA thread with a live dispatcher, because the feedback helper awaits a delay and
/// its continuation has to land back on the thread that owns the button.
/// </remarks>
public class UxPolishTests
{
    // =============================================================================================================
    // The world.
    // =============================================================================================================

    private sealed class UxWorld : IDisposable
    {
        public ScenarioAgentClient Agent { get; } = new();
        public ScenarioCpuPower Cpu { get; } = new();
        public ScenarioDisplay Display { get; } = new();
        public ScenarioBrightness Brightness { get; } = new();
        public InMemoryPowerScheme Scheme { get; } = new();
        public AppSettings Settings { get; } = new();
        public BatteryWatchdogService Battery { get; }
        public MainWindow Window { get; }

        public bool OnAc = true;
        public int BatteryPercent = 90;

        public UxWorld()
        {
            Battery = new BatteryWatchdogService(attachSystemEvents: false)
            {
                SuppressSounds = true,
                PowerStatusSeam = () => (true, (byte)(OnAc ? 1 : 0), (byte)BatteryPercent)
            };

            Window = new MainWindow(
                Agent, Cpu, new GameDetectionService(), new AtkMouseService(),
                batteryService: Battery,
                settings: Settings,
                isTestHarness: true,
                displayService: Display,
                screenBrightnessService: Brightness,
                schoolModeScheme: Scheme);

            Window.PowerStateProvider = () => (OnAc, BatteryPercent);
            Window.ToastHandler = (_, _) => { };
            Window.MouseProfileSwitcher = _ => true;
            Window.SchoolModeServiceForTesting!.BackupPath =
                AppPaths.GetDataFile($"ux_school_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            try { Battery.Dispose(); } catch { }
            try { Window.Close(); } catch { }
        }
    }

    // =============================================================================================================
    // 1. In-button feedback.
    // =============================================================================================================

    [Fact]
    public async Task ButtonFeedback_ShowsProgressThenSuccessThenRestoresTheLabel()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.ButtonFeedbackHold = TimeSpan.FromMilliseconds(400);

            var button = new WpfButton { Content = "Servisi Onar", IsEnabled = true };
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var run = world.Window.RunWithButtonFeedbackAsync(button, "Servisi Onar", () => gate.Task);

            // The helper disables and relabels before it awaits, so the busy state is visible the instant the click
            // handler returns — which is the whole point: the user must not be able to press it twice.
            Assert.False(button.IsEnabled);
            Assert.Equal(Loc.T("Uygulanıyor…"), button.Content as string);
            Assert.True(world.Window.IsButtonFeedbackActiveForTesting(button));

            gate.SetResult(true);
            await WaitUntilAsync(() => (button.Content as string) == Loc.T("✓ Tamamlandı"));

            Assert.False(button.IsEnabled);
            Assert.True(await run);

            Assert.Equal("Servisi Onar", button.Content as string);
            Assert.True(button.IsEnabled);
            Assert.False(world.Window.IsButtonFeedbackActiveForTesting(button));
        });
    }

    [Fact]
    public async Task ButtonFeedback_ReportsFailureWhenTheActionSaysSo()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.ButtonFeedbackHold = TimeSpan.FromMilliseconds(400);

            var button = new WpfButton { Content = "Optimize Et", IsEnabled = true };
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var run = world.Window.RunWithButtonFeedbackAsync(button, "Optimize Et", () => gate.Task);
            gate.SetResult(false);

            await WaitUntilAsync(() => (button.Content as string) == Loc.T("! Başarısız"));
            Assert.False(await run);
            Assert.Equal("Optimize Et", button.Content as string);
            Assert.True(button.IsEnabled);
        });
    }

    [Fact]
    public async Task ButtonFeedback_AnActionThatThrows_StillEndsInFailureAndRestores()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.ButtonFeedbackHold = TimeSpan.FromMilliseconds(50);

            var button = new WpfButton { Content = "Kaydı Onar", IsEnabled = true };
            bool ok = await world.Window.RunWithButtonFeedbackAsync(button, "Kaydı Onar",
                () => throw new InvalidOperationException("service died"));

            Assert.False(ok);
            Assert.Equal("Kaydı Onar", button.Content as string);
            Assert.True(button.IsEnabled);
        });
    }

    [Fact]
    public async Task ButtonFeedback_SecondPress_CancelsTheFirstRestore()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.ButtonFeedbackHold = TimeSpan.FromMilliseconds(300);

            var button = new WpfButton { Content = "Yedekle", IsEnabled = true };

            var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = world.Window.RunWithButtonFeedbackAsync(button, "Yedekle", () => firstGate.Task);
            firstGate.SetResult(true);
            await WaitUntilAsync(() => (button.Content as string) == Loc.T("✓ Tamamlandı"));

            // Pressed again while the first outcome is still on screen.
            var secondGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = world.Window.RunWithButtonFeedbackAsync(button, "Yedekle", () => secondGate.Task);

            Assert.Equal(Loc.T("Uygulanıyor…"), button.Content as string);

            // Past the first run's hold: its restore must have been cancelled, not merely delayed.
            await Task.Delay(500);
            Assert.Equal(Loc.T("Uygulanıyor…"), button.Content as string);
            Assert.False(button.IsEnabled);

            secondGate.SetResult(true);
            await WaitUntilAsync(() => (button.Content as string) == Loc.T("✓ Tamamlandı"));
            await second;

            Assert.Equal("Yedekle", button.Content as string);
            Assert.True(button.IsEnabled);

            // The cancelled run ends without touching the label.
            Assert.True(await first);
        });
    }

    [Fact]
    public async Task ButtonFeedback_LanguageChange_PutsTheCatalogueLabelBack()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.ButtonFeedbackHold = TimeSpan.FromSeconds(30);

            var button = new WpfButton { Content = "Geri Al", IsEnabled = true };
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = world.Window.RunWithButtonFeedbackAsync(button, "Geri Al", () => gate.Task);

            Assert.Equal(Loc.T("Uygulanıyor…"), button.Content as string);

            world.Window.RestoreAllButtonFeedback();

            Assert.Equal("Geri Al", button.Content as string);
            Assert.True(button.IsEnabled);
            Assert.False(world.Window.IsButtonFeedbackActiveForTesting(button));

            gate.SetResult(true);
        });
    }

    [Fact]
    public async Task ButtonFeedback_KeepsARichContentIntact()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.ButtonFeedbackHold = TimeSpan.FromMilliseconds(50);

            var inner = new TextBlock { Text = "Geri yükle" };
            var button = new WpfButton { Content = inner, IsEnabled = true };

            await world.Window.RunWithButtonFeedbackAsync(button, "Geri yükle", () => Task.FromResult(true));

            // A panel or icon is put back by reference, not rebuilt from the catalogue.
            Assert.Same(inner, button.Content);
            Assert.True(button.IsEnabled);
        });
    }

    [Fact]
    public async Task ButtonFeedback_WithoutAButton_JustRunsTheAction()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            bool ran = false;
            bool ok = await world.Window.RunWithButtonFeedbackAsync(null, "Servisi Onar", () =>
            {
                ran = true;
                return Task.FromResult(true);
            });

            Assert.True(ran);
            Assert.True(ok);
        });
    }

    // =============================================================================================================
    // 2. Apply the last picked colour to all four zones.
    // =============================================================================================================

    [Fact]
    public async Task ApplyAllZones_SpreadsTheLastPickedColour_NotTheOneInZoneOne()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();

            // First pick: this is what "apply all" has to remember.
            world.Window.SetLightingForTesting(new LightingConfig
            {
                Effect = "STATIC", Brightness = 6, Speed = 3, Direction = 1, Color = "#112233",
                LEDs = new() { "#112233", "#112233", "#112233", "#112233" }
            });
            await world.Window.ApplyLastPickedColorToAllZonesAsync();

            // A zone was then given its own colour, and zone 1 drifted with it.
            world.Window.SetLightingForTesting(new LightingConfig
            {
                Effect = "STATIC", Brightness = 6, Speed = 3, Direction = 1, Color = "#AABBCC",
                LEDs = new() { "#AABBCC", "#445566", "#AABBCC", "#AABBCC" }
            });

            await world.Window.ApplyLastPickedColorToAllZonesAsync();

            var lighting = world.Window.LightingForTesting;
            Assert.Equal(4, lighting.LEDs.Count);
            Assert.All(lighting.LEDs, led => Assert.Equal("#112233", led));
            Assert.Equal("#112233", lighting.Color);
        });
    }

    [Fact]
    public async Task ApplyAllZones_WhenTheLightsAreOff_BringsThemBackLit()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();

            world.Window.SetLightingForTesting(new LightingConfig
            {
                Effect = "STATIC", Brightness = 6, Speed = 3, Direction = 1, Color = "#112233",
                LEDs = new() { "#112233", "#112233", "#112233", "#112233" }
            });
            await world.Window.ApplyLastPickedColorToAllZonesAsync();

            world.Settings.LastActiveLighting = new LightingConfig
            {
                Effect = "BREATHING", Brightness = 7, Speed = 2, Direction = 1, Color = "#FF2A55",
                LEDs = new() { "#FF2A55", "#FF2A55", "#FF2A55", "#FF2A55" }
            };
            world.Window.SetLightingForTesting(new LightingConfig
            {
                Effect = "OFF", Brightness = 0, Speed = 3, Direction = 1, Color = "#000000",
                LEDs = new() { "#000000", "#000000", "#000000", "#000000" }
            });

            await world.Window.ApplyLastPickedColorToAllZonesAsync();

            var lighting = world.Window.LightingForTesting;
            Assert.True(lighting.Brightness > 0);
            Assert.NotEqual("OFF", lighting.Effect);
            Assert.All(lighting.LEDs, led => Assert.Equal("#112233", led));
        });
    }

    [Fact]
    public async Task ApplyAllZones_WithNothingEverPicked_FallsBackToTheDefaultColour()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.SetLightingForTesting(new LightingConfig
            {
                Effect = "OFF", Brightness = 0, Speed = 3, Direction = 1, Color = "#000000",
                LEDs = new() { "#000000", "#000000", "#000000", "#000000" }
            });

            await world.Window.ApplyLastPickedColorToAllZonesAsync();

            var lighting = world.Window.LightingForTesting;
            Assert.All(lighting.LEDs, led => Assert.Equal(LightingPolicy.DefaultColor, led));
            Assert.True(lighting.Brightness > 0);
        });
    }

    // =============================================================================================================
    // 3. The MOD badge menu.
    // =============================================================================================================

    [Fact]
    public async Task ModeMenu_HidesAModeTheModelDoesNotOffer()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Agent.SupportedModes = new[] { OperatingMode.Quiet, OperatingMode.Default };
            await world.Window.RefreshModeCapabilityAsync();
            world.Window.SetCurrentModeForTesting(OperatingMode.Default);

            var menu = world.Window.BuildModeMenu();
            var offered = menu.Items.Cast<WpfMenuItem>().Select(i => (OperatingMode)i.Tag!).ToList();

            Assert.Contains(OperatingMode.Quiet, offered);
            Assert.Contains(OperatingMode.Default, offered);
            Assert.DoesNotContain(OperatingMode.Turbo, offered);
            Assert.DoesNotContain(OperatingMode.Extreme, offered);
        });
    }

    [Fact]
    public async Task ModeMenu_KeepsTheActiveModeVisibleEvenWhenItsGroupIsHidden()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Agent.SupportedModes = new[] { OperatingMode.Quiet, OperatingMode.Default };
            await world.Window.RefreshModeCapabilityAsync();
            world.Window.SetCurrentModeForTesting(OperatingMode.Turbo);

            var menu = world.Window.BuildModeMenu();
            var turbo = menu.Items.Cast<WpfMenuItem>().SingleOrDefault(i => (OperatingMode)i.Tag! == OperatingMode.Turbo);

            // A menu with no marked row would leave the user guessing which mode the machine is in.
            Assert.NotNull(turbo);
            Assert.True(turbo!.IsChecked);
        });
    }

    [Fact]
    public async Task ModeMenu_GreysOutTheHighPowerPairOnBattery()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.OnAc = false;
            world.BatteryPercent = 80;
            world.Window.SetCurrentModeForTesting(OperatingMode.Default);

            var menu = world.Window.BuildModeMenu();
            var items = menu.Items.Cast<WpfMenuItem>().ToList();

            foreach (var mode in new[] { OperatingMode.Extreme, OperatingMode.Turbo })
            {
                var item = items.SingleOrDefault(i => (OperatingMode)i.Tag! == mode);
                if (item == null) continue;
                Assert.False(item.IsEnabled);
                Assert.Equal(ModePolicy.HighPowerBlockedMessage, item.ToolTip as string);
            }

            Assert.True(items.Single(i => (OperatingMode)i.Tag! == OperatingMode.Quiet).IsEnabled);
        });
    }

    [Fact]
    public async Task ModeMenu_ChecksTheModeTheMachineIsIn()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.SetCurrentModeForTesting(OperatingMode.Quiet);

            var menu = world.Window.BuildModeMenu();
            var checkedItems = menu.Items.Cast<WpfMenuItem>().Where(i => i.IsChecked).ToList();

            Assert.Single(checkedItems);
            Assert.Equal(OperatingMode.Quiet, (OperatingMode)checkedItems[0].Tag!);
        });
    }

    [Fact]
    public async Task ModeMenu_AnItemGoesThroughSetManualModeAsync()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Window.SetCurrentModeForTesting(OperatingMode.Default);

            var menu = world.Window.BuildModeMenu();
            var quiet = menu.Items.Cast<WpfMenuItem>().Single(i => (OperatingMode)i.Tag! == OperatingMode.Quiet);

            quiet.RaiseEvent(new RoutedEventArgs(WpfMenuItem.ClickEvent));
            await WaitUntilAsync(() => world.Agent.ModeWrites.Contains(OperatingMode.Quiet));

            Assert.Contains(OperatingMode.Quiet, world.Agent.ModeWrites);
            Assert.Equal(OperatingMode.Quiet, world.Window.CurrentMode);
        });
    }

    [Fact]
    public async Task ModeMenu_IsRefusedWhileSchoolModeOwnsTheMachine()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Settings.SchoolModeEnabled = true;
            world.Window.SetCurrentModeForTesting(OperatingMode.Default);

            var menu = world.Window.BuildModeMenu();
            var turbo = menu.Items.Cast<WpfMenuItem>().Single(i => (OperatingMode)i.Tag! == OperatingMode.Turbo);

            turbo.RaiseEvent(new RoutedEventArgs(WpfMenuItem.ClickEvent));
            await Task.Delay(150);

            // The menu is a second door into the same guard: school mode must not be bypassed from the title bar.
            Assert.DoesNotContain(OperatingMode.Turbo, world.Agent.ModeWrites);
            Assert.Equal(OperatingMode.Default, world.Window.CurrentMode);
        });
    }

    // =============================================================================================================
    // 4. The collapsible temperature strip.
    // =============================================================================================================

    [Fact]
    public async Task TraceToggle_FoldsTheChartAndFlipsTheLabel()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            var window = world.Window;

            // The harness window has no XAML, so the three controls the toggle touches are supplied here.
            window.TraceChartBorder = new Border { Visibility = Visibility.Visible };
            window.TxtToggleTrace = new TextBlock();
            window.BtnToggleTrace = new WpfButton();

            window.SetModeTraceCollapsed(true, persist: false);

            Assert.Equal(Visibility.Collapsed, window.TraceChartBorder.Visibility);
            Assert.Equal(Loc.T("GÖSTER"), window.TxtToggleTrace.Text);
            Assert.Equal(Loc.T("Grafiği göster"), window.BtnToggleTrace.ToolTip as string);
            Assert.True(world.Settings.ModeTraceCollapsed);

            window.SetModeTraceCollapsed(false, persist: false);

            Assert.Equal(Visibility.Visible, window.TraceChartBorder.Visibility);
            Assert.Equal(Loc.T("GİZLE"), window.TxtToggleTrace.Text);
            Assert.Equal(Loc.T("Grafiği gizle"), window.BtnToggleTrace.ToolTip as string);
            Assert.False(world.Settings.ModeTraceCollapsed);
        });
    }

    [Fact]
    public async Task TraceToggle_WithoutTheControls_StillRemembersThePreference()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();

            // A window that failed to load its XAML must not throw on the way out of initialisation.
            world.Window.SetModeTraceCollapsed(true, persist: false);

            Assert.True(world.Settings.ModeTraceCollapsed);
        });
    }

    // =============================================================================================================
    // 5. The clickable left-rail rows.
    // =============================================================================================================

    [Theory]
    [InlineData("Monitoring", "Monitoring")]
    [InlineData("Modes", "Modes")]
    [InlineData("Boost", "Boost")]
    [InlineData("", null)]
    [InlineData("Nonsense", null)]
    public async Task SideStatRow_MapsTheTagToAPage(string tag, string? expected)
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            Assert.Equal(expected, world.Window.SideStatRowTargetFor(tag));
        });
    }

    [Fact]
    public async Task SideStatRow_MouseRow_FollowsTheAtkIntegrationSwitch()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();

            world.Settings.AtkMouseIntegrationEnabled = true;
            Assert.Equal("Mouse", world.Window.SideStatRowTargetFor("Mouse"));

            // With the integration off there is no mouse profile to configure, so the row goes to the tools page
            // where it can be switched on rather than to a page whose mouse section is hidden.
            world.Settings.AtkMouseIntegrationEnabled = false;
            Assert.Equal("Debloat", world.Window.SideStatRowTargetFor("Mouse"));
        });
    }

    // =============================================================================================================
    // 6. The BOOST and FARE menus.
    // =============================================================================================================

    [Fact]
    public async Task BoostMenu_OffersTheThreeLevelsAndChecksTheCurrentOne()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Cpu.SetCurrentModeForTesting(CpuBoostMode.Efficient);

            var menu = world.Window.BuildBoostMenu();
            var items = menu.Items.Cast<WpfMenuItem>().ToList();

            Assert.Equal(3, items.Count);
            Assert.Equal(
                new[] { CpuBoostMode.Aggressive, CpuBoostMode.Efficient, CpuBoostMode.Disabled },
                items.Select(i => (CpuBoostMode)i.Tag!).ToArray());

            var checkedItems = items.Where(i => i.IsChecked).ToList();
            Assert.Single(checkedItems);
            Assert.Equal(CpuBoostMode.Efficient, (CpuBoostMode)checkedItems[0].Tag!);
        });
    }

    [Fact]
    public async Task BoostMenu_AnItemGoesThroughSetManualCpuBoostAsync()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Cpu.Mode = CpuBoostMode.Aggressive;

            var menu = world.Window.BuildBoostMenu();
            var disabled = menu.Items.Cast<WpfMenuItem>().Single(i => (CpuBoostMode)i.Tag! == CpuBoostMode.Disabled);

            disabled.RaiseEvent(new RoutedEventArgs(WpfMenuItem.ClickEvent));
            await WaitUntilAsync(() => world.Cpu.Writes.Contains(CpuBoostMode.Disabled));

            // The menu is a second door into the same call the segmented buttons use, so the desktop baseline is
            // updated exactly as it is from the page.
            Assert.Contains(CpuBoostMode.Disabled, world.Cpu.Writes);
            Assert.Equal(CpuBoostMode.Disabled, world.Cpu.Mode);
        });
    }

    [Fact]
    public async Task MouseProfileMenu_OffersTheFiveChoicesAndChecksTheSavedOne()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Settings.DefaultAtkMouseProfile = 3;

            var menu = world.Window.BuildMouseProfileMenu();
            var items = menu.Items.Cast<WpfMenuItem>().ToList();

            Assert.Equal(5, items.Count);
            Assert.Equal(
                new[] { 1, 2, 3, 4, 0 },
                items.Select(i => (int)i.Tag!).ToArray());

            var checkedItems = items.Where(i => i.IsChecked).ToList();
            Assert.Single(checkedItems);
            Assert.Equal(3, (int)checkedItems[0].Tag!);
        });
    }

    [Fact]
    public async Task MouseProfileMenu_AnItemSavesAndAppliesTheProfile()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Settings.AtkMouseIntegrationEnabled = true;
            world.Settings.DefaultAtkMouseProfile = 1;

            var applied = new List<int>();
            world.Window.MouseProfileSwitcher = profile =>
            {
                applied.Add(profile);
                return true;
            };

            var menu = world.Window.BuildMouseProfileMenu();
            var config2 = menu.Items.Cast<WpfMenuItem>().Single(i => (int)i.Tag! == 2);

            config2.RaiseEvent(new RoutedEventArgs(WpfMenuItem.ClickEvent));
            await WaitUntilAsync(() => applied.Contains(2));

            Assert.Contains(2, applied);
            Assert.Equal(2, world.Settings.DefaultAtkMouseProfile);
        });
    }

    [Fact]
    public async Task MouseProfileMenu_KeepLastUsed_IsSavedWithoutTouchingTheMouse()
    {
        await RunStaAsync(async () =>
        {
            using var world = new UxWorld();
            world.Settings.AtkMouseIntegrationEnabled = true;
            world.Settings.DefaultAtkMouseProfile = 2;

            var applied = new List<int>();
            world.Window.MouseProfileSwitcher = profile =>
            {
                applied.Add(profile);
                return true;
            };

            var menu = world.Window.BuildMouseProfileMenu();
            var keepLast = menu.Items.Cast<WpfMenuItem>().Single(i => (int)i.Tag! == 0);

            keepLast.RaiseEvent(new RoutedEventArgs(WpfMenuItem.ClickEvent));
            await WaitUntilAsync(() => world.Settings.DefaultAtkMouseProfile == 0);

            // "Keep the last one" is a preference, not a profile: nothing is sent to the hardware.
            Assert.Empty(applied);
            Assert.Equal(0, world.Settings.DefaultAtkMouseProfile);
        });
    }

    [Fact]
    public void MouseProfileIndex_MatchesTheComboOrder()
    {
        Assert.Equal(0, MainWindow.DefaultMouseProfileIndex(1));
        Assert.Equal(1, MainWindow.DefaultMouseProfileIndex(2));
        Assert.Equal(2, MainWindow.DefaultMouseProfileIndex(3));
        Assert.Equal(3, MainWindow.DefaultMouseProfileIndex(4));
        Assert.Equal(4, MainWindow.DefaultMouseProfileIndex(0));
    }

    // =============================================================================================================
    // Harness.
    // =============================================================================================================

    /// <summary>
    /// Runs a body on a dedicated STA thread with a live dispatcher, so every continuation after an <c>await</c>
    /// lands back on the thread that owns the WPF objects.
    /// </summary>
    private static Task RunStaAsync(Func<Task> body)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds, or fails the test after two seconds.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("The condition did not become true within two seconds.");
    }
}

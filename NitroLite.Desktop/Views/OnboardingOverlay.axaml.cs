using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NitroLite.Desktop.ViewModels;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The three-step walkthrough shown on first launch.
/// </summary>
/// <remarks>
/// A layer inside the window rather than a window of its own: the shell stays visible behind it, so the user can see
/// the app they are being asked to configure. All the state lives on <c>MainViewModel</c>; the code here is only the
/// keyboard: Enter moves on (or finishes on the last step), Escape skips, and the focus starts on the button Enter
/// would press, so the walkthrough can be finished without a mouse.
/// </remarks>
public partial class OnboardingOverlay : UserControl
{
    public OnboardingOverlay()
    {
        InitializeComponent();

        // Bubbling and only when unhandled: a focused button answers Enter itself (Back, Skip, the switch), and this must
        // not press Next on top of it.
        AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Bubble);
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsEffectivelyVisible || DataContext is not MainViewModel vm) return;

        ICommand? command = e.Key switch
        {
            Key.Escape => vm.OnboardingSkipCommand,
            Key.Enter => vm.IsOnboardingLastStep ? vm.OnboardingFinishCommand : vm.OnboardingNextCommand,
            _ => null
        };

        if (command == null || !command.CanExecute(null)) return;

        command.Execute(null);
        e.Handled = true;
        FocusPrimary();
    }

    /// <summary>
    /// Puts the focus on the step's primary button (Next, or Finish on the last step). Called when the walkthrough
    /// opens: until then the focus is somewhere in the shell underneath, which is out of the tab order meanwhile.
    /// </summary>
    public void FocusPrimary()
    {
        var target = NextButton is { IsEffectivelyVisible: true } ? NextButton : FinishButton;
        target?.Focus(NavigationMethod.Tab);
    }
}

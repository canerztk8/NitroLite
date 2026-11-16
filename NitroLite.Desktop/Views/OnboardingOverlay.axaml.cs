using Avalonia.Controls;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The three-step walkthrough shown on first launch.
/// </summary>
/// <remarks>
/// A layer inside the window rather than a window of its own: the shell stays visible behind it, so the user can see
/// the app they are being asked to configure. All the state lives on <c>MainViewModel</c>, which is why this class has
/// no code beyond the XAML load.
/// </remarks>
public partial class OnboardingOverlay : UserControl
{
    public OnboardingOverlay()
    {
        InitializeComponent();
    }
}

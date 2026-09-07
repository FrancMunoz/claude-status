using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>
/// The details popup shown by a left click on the tray icon.
/// </summary>
/// <remarks>
/// Closes when it loses focus, the way a tray popup is expected to behave. It
/// hides rather than closes so the view model's subscription survives and the
/// next open is instant.
/// </remarks>
public partial class DetailsWindow : Window
{
    public DetailsWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDeactivated(object? sender, System.EventArgs e) => Hide();
}

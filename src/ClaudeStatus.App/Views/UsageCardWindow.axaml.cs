using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>The widget's hover card. Shown, placed and hidden by <see cref="Tray.TaskbarWidgetIndicator"/>.</summary>
public partial class UsageCardWindow : Window
{
    public UsageCardWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

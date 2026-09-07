using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>
/// The window that becomes the taskbar widget. All behaviour lives in
/// <see cref="Tray.TaskbarWidgetIndicator"/>; this is wiring only.
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    public TaskbarWidgetWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

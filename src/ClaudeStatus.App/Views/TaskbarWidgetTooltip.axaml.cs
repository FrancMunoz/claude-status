using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>The hover card the taskbar widget shows. Wiring only.</summary>
public partial class TaskbarWidgetTooltip : UserControl
{
    public TaskbarWidgetTooltip()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

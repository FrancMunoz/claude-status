using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>The about window: version, data source, and the unofficial-source notice.</summary>
public partial class InfoWindow : Window
{
    public InfoWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

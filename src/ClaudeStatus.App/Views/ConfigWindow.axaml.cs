using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>The configuration window. Opens automatically on first run.</summary>
public partial class ConfigWindow : Window
{
    public ConfigWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

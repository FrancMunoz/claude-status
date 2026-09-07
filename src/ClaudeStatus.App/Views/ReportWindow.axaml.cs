using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeStatus.App.Views;

/// <summary>
/// The full report window, opened from the tray menu or from the details popup.
/// </summary>
/// <remarks>
/// Unlike <see cref="DetailsWindow"/>, this one does <b>not</b> hide on
/// deactivate. The popup is a glance you dismiss; this is a reference you keep
/// open beside claude.ai while comparing numbers, and closing it on focus loss
/// would make that impossible.
/// </remarks>
public partial class ReportWindow : Window
{
    public ReportWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

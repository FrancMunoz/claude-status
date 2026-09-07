using System;
using Avalonia;
using Velopack;

namespace ClaudeStatus.App;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must be the very first thing this process does.
        //
        // The installer, the updater and the uninstaller all re-run this same
        // executable with hook arguments, and Run() handles those and exits
        // before any of them reaches Avalonia. Starting a window first would
        // flash a tray icon during install and leave the hook unhandled; vpk
        // refuses to package a build where this call is missing, which is how
        // that mistake gets caught rather than shipped.
        //
        // Auto-apply on startup is left at its default of on: an update
        // downloaded in the background is installed the next time the app
        // starts, which is what makes "check now, apply on quit" work without
        // ever restarting the app underneath someone.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

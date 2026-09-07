using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClaudeStatus.App.Composition;
using ClaudeStatus.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeStatus.App;

/// <summary>
/// The application. Headless by design: there is no main window.
/// </summary>
/// <remarks>
/// <see cref="ShutdownMode.OnExplicitShutdown"/> is essential here. Under the
/// default mode Avalonia would exit as soon as the last window closed, and this
/// app spends nearly all of its life with no window open at all.
/// </remarks>
public partial class App : Application, IDisposable
{
    private ServiceProvider? _services;
    private TrayApplicationController? _controller;
    private SingleInstanceGuard? _instanceGuard;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No MainWindow is assigned on purpose.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _services = new ServiceCollection().AddClaudeStatus().BuildServiceProvider();

            // Autostart makes a second copy easy to trigger: log in with the app
            // already running, or launch it by hand after it has autostarted. Two
            // tray icons is a visible bug, so the loser exits quietly.
            _instanceGuard = SingleInstanceGuard.Acquire(
                _services.GetRequiredService<IPlatformInfo>().ConfigDirectory,
                _services.GetRequiredService<ILogger<SingleInstanceGuard>>());

            if (!_instanceGuard.ShouldRun)
            {
                // Posted rather than called: this runs before the main loop has
                // started, and shutting the dispatcher down from here leaves the
                // loop to start on a dead dispatcher and throw. Queueing it makes
                // the loop begin, process this, and exit 0 - which is what "the
                // loser exits quietly" was always meant to mean.
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _controller = new TrayApplicationController(_services, desktop);

            desktop.ShutdownRequested += (_, _) => Dispose();

            // Not awaited: startup reads the config file and begins polling, and
            // blocking here would stall the UI thread before the tray appears.
            //
            // But not discarded either. A bare `_ = StartAsync()` swallows every
            // failure after the first log line into an unobserved Task - the tray
            // icon appears, part of the app silently never starts, and the log
            // ends mid-sentence with no clue why. That is exactly how the update
            // checker's absence went unexplained on 2026-09-05.
            _ = StartControllerAsync(_controller, _services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Runs the controller's startup and logs anything it throws.
    /// </summary>
    /// <remarks>
    /// The app deliberately survives a failure here rather than exiting: the tray
    /// icon is already visible by this point, and a user is better served by an
    /// icon that reports "no data" than by one that disappears. What must not
    /// happen is the failure going unrecorded.
    /// </remarks>
    private static async Task StartControllerAsync(
        TrayApplicationController controller, IServiceProvider services)
    {
        try
        {
            await controller.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>().LogError(
                ex, "Startup did not complete. The app is running, but degraded.");
        }
    }

    /// <summary>
    /// Releases the controller and the service graph.
    /// </summary>
    /// <remarks>
    /// Avalonia never disposes the <see cref="Application"/> itself, so the real
    /// teardown happens on <c>ShutdownRequested</c> above. This exists so the
    /// ownership is declared rather than implied, and is safe to call twice.
    /// </remarks>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _controller?.Dispose();
        _controller = null;
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        _services?.Dispose();
        _services = null;
    }
}

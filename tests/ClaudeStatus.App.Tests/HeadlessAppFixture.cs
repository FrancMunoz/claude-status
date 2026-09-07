using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ClaudeStatus.App;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Boots Avalonia once, headlessly, so windows can actually be constructed.
/// </summary>
/// <remarks>
/// <para>
/// This is the only way to catch a broken AXAML file or an unresolvable binding:
/// both compile fine and fail at load. A build passing is not evidence that a
/// window opens.
/// </para>
/// <para>
/// Avalonia allows exactly one application per process, so this is a shared
/// fixture and everything that touches UI types runs inside
/// <see cref="Invoke"/> on its dispatcher thread.
/// </para>
/// </remarks>
public sealed class HeadlessAppFixture : IDisposable
{
    private readonly Thread _uiThread;
    private readonly TaskCompletionSource _ready = new();
    private CancellationTokenSource? _shutdown;

    public HeadlessAppFixture()
    {
        _uiThread = new Thread(Run) { IsBackground = true, Name = "avalonia-headless" };

        // STA is a Windows COM concept: the call throws PlatformNotSupportedException
        // on macOS and Linux. Avalonia's headless backend does not need it there.
        if (OperatingSystem.IsWindows())
        {
            _uiThread.SetApartmentState(ApartmentState.STA);
        }

        _uiThread.Start();

        // Fail loudly rather than hanging the whole suite if startup breaks.
        if (!_ready.Task.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The headless Avalonia session did not start.");
        }
    }

    private void Run()
    {
        try
        {
            _shutdown = new CancellationTokenSource();
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            _ready.SetResult();
            Dispatcher.UIThread.MainLoop(_shutdown.Token);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the Avalonia UI thread and waits.</summary>
    public static void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.InvokeAsync(action).GetTask().GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="func"/> on the Avalonia UI thread and returns its value.</summary>
    public static T Invoke<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Dispatcher.UIThread.InvokeAsync(func).GetTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown?.Cancel();
        _shutdown?.Dispose();
        _uiThread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>Shares one Avalonia session across every UI test.</summary>
[CollectionDefinition(HeadlessTests.Name)]
public sealed class HeadlessTests : ICollectionFixture<HeadlessAppFixture>
{
    public const string Name = "avalonia-headless";
}

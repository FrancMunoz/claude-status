using ClaudeStatus.Platform;
using ClaudeStatus.Platform.Windows;
using Microsoft.Win32;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The Windows taskbar theme provider, against a scratch key in the real registry.
/// </summary>
/// <remarks>
/// Never the real Personalize key: flipping it would change the theme of the
/// machine running the tests. The scratch key lives under <c>HKCU</c> and is
/// deleted afterwards, including on failure.
/// </remarks>
public sealed class WindowsTrayThemeProviderTests : IDisposable
{
    /// <summary>How long a registry notification may take. It is milliseconds in practice.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _keyPath = @"Software\ClaudeStatus.Tests\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        }
    }

    private void SetLight(bool light)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(WindowsTrayThemeProvider.SystemThemeValue, light ? 1 : 0, RegistryValueKind.DWord);
    }

    [Fact]
    public void Reads_the_value()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        using var provider = new WindowsTrayThemeProvider(_keyPath);
        provider.Current.Should().Be(TrayBackground.Unknown, "the key does not exist yet");

        SetLight(true);
        provider.Current.Should().Be(TrayBackground.Light);

        SetLight(false);
        provider.Current.Should().Be(TrayBackground.Dark);
    }

    [Fact]
    public void Raises_Changed_on_every_flip()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        SetLight(false);
        using var provider = new WindowsTrayThemeProvider(_keyPath);
        using var raised = new AutoResetEvent(false);
        provider.Changed += (_, _) => raised.Set();

        // Twice, because the notification is spent when it fires and has to be
        // re-armed - the second flip is the one that proves it was.
        SetLight(true);
        raised.WaitOne(Patience).Should().BeTrue("the taskbar went light");
        provider.Current.Should().Be(TrayBackground.Light);

        SetLight(false);
        raised.WaitOne(Patience).Should().BeTrue("the taskbar went dark again");
        provider.Current.Should().Be(TrayBackground.Dark);
    }

    [Fact]
    public void Ignores_other_values_in_the_key()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        SetLight(false);
        using var provider = new WindowsTrayThemeProvider(_keyPath);
        using var raised = new AutoResetEvent(false);
        provider.Changed += (_, _) => raised.Set();

        // The real key also holds accent and transparency settings.
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath))
        {
            key.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);
        }

        raised.WaitOne(TimeSpan.FromMilliseconds(500)).Should().BeFalse();
    }

    [Fact]
    public void Stops_raising_once_unsubscribed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        SetLight(false);
        using var provider = new WindowsTrayThemeProvider(_keyPath);
        using var raised = new AutoResetEvent(false);
        EventHandler handler = (_, _) => raised.Set();
        provider.Changed += handler;
        provider.Changed -= handler;

        SetLight(true);

        raised.WaitOne(TimeSpan.FromMilliseconds(500)).Should().BeFalse();
    }
}

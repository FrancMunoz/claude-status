using System.Runtime.Versioning;
using ClaudeStatus.Platform;
using Microsoft.Win32;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Starts at login through the per-user <c>Run</c> key.
/// </summary>
/// <remarks>
/// <para>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Per-user, so it
/// needs no elevation and affects nobody else on the machine.
/// </para>
/// <para>
/// The value is the executable path in quotes. Quoting matters: an unquoted path
/// containing a space is parsed as a command plus arguments, and
/// <c>C:\Program Files\…</c> is the normal install location.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutostart : IAutostart
{
    /// <summary>The per-user Run key.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name our entry uses.</summary>
    public const string ValueName = "ClaudeStatus";

    private readonly IPlatformInfo _platform;

    public RegistryAutostart(IPlatformInfo platform)
        => _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    /// <inheritdoc />
    public string DescriptionKey => "Autostart_WindowsRegistry";

    /// <inheritdoc />
    public bool IsSupported => _platform.ExecutablePath is not null;

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return Task.FromResult(key?.GetValue(ValueName) is not null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Locked down by policy. Report "off" rather than crashing the app.
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (enabled && _platform.ExecutablePath is null)
        {
            throw new AutostartException(
                "Autostart_Error_PathUnknown",
                "Could not determine the ClaudeStatus executable path, so autostart cannot be enabled.");
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new AutostartException(
                    "Autostart_Error_RunKey", "The Windows Run key could not be opened.");

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{_platform.ExecutablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            throw new AutostartException(
                "Autostart_Error_Write",
                "Windows refused the autostart change. It may be blocked by policy.", ex);
        }
    }
}

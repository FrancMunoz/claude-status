using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeStatus.Platform;

/// <summary>
/// The parts of <see cref="IPlatformInfo"/> that are identical everywhere.
/// </summary>
/// <remarks>
/// Resolving our own executable path is one of them, and it is fiddly enough to
/// be worth doing once: a single-file or Velopack-packaged build reports a host
/// path from <see cref="Environment.ProcessPath"/> that differs from the managed
/// assembly location, and the host path is the one an OS autostart entry needs.
/// </remarks>
public abstract class PlatformInfoBase : IPlatformInfo
{
    /// <summary>The folder name used under the per-OS config root.</summary>
    protected const string ApplicationFolderName = "ClaudeStatus";

    /// <inheritdoc />
    public abstract PlatformKind Kind { get; }

    /// <inheritdoc />
    public abstract string ConfigDirectory { get; }

    /// <inheritdoc />
    public virtual TraySupport TraySupport => TraySupport.Available;

    /// <inheritdoc />
    public virtual string OperatingSystemName => RuntimeInformation.OSDescription.Trim();

    /// <inheritdoc />
    /// <remarks>False unless a platform says otherwise: a square icon is what every
    /// tray is guaranteed to accept.</remarks>
    public virtual bool SupportsInlineTrayText => false;

    /// <inheritdoc />
    /// <remarks>False unless a platform says otherwise, leaving the popup to infer
    /// the corner from the screen insets as it always has.</remarks>
    public virtual bool TrayIsAtTop => false;

    /// <inheritdoc />
    /// <remarks>
    /// Prefers <see cref="Environment.ProcessPath"/> - for a single-file build
    /// that is the real executable, which is what an autostart entry must point
    /// at. Falls back to the main module, then gives up rather than registering
    /// a path that will not launch.
    /// </remarks>
    public virtual string? ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return path;
            }

            try
            {
                using Process current = Process.GetCurrentProcess();
                string? module = current.MainModule?.FileName;
                return !string.IsNullOrEmpty(module) && File.Exists(module) ? module : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Creates the config directory if it is not there yet.</summary>
    public string EnsureConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
        return ConfigDirectory;
    }
}

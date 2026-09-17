using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Asks the shell whether an AppUserModelID belongs to an installed app.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class AppUserModelIds
{
    /// <summary>IID_IShellItem.</summary>
    private static readonly Guid ShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>
    /// Whether a Start Menu entry carries <paramref name="appUserModelId"/>.
    /// </summary>
    /// <remarks>
    /// <c>shell:AppsFolder</c> is the virtual folder Start is built from, keyed by
    /// app id - the same list <c>Get-StartApps</c> prints. Parsing a name inside it
    /// succeeds exactly when such an app exists, which is the condition Windows
    /// applies before it will show a toast for that id.
    /// </remarks>
    public static bool IsRegistered(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        try
        {
            Guid iid = ShellItem;
            int result = SHCreateItemFromParsingName(@"shell:AppsFolder\" + appUserModelId, 0, ref iid, out nint item);
            if (item != 0)
            {
                Marshal.Release(item);
            }

            return result >= 0 && item != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, out nint item);
}

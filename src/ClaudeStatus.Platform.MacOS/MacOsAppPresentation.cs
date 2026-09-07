using System.Runtime.Versioning;
using ClaudeStatus.Platform.MacOS.Interop;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Turns the process into a menu bar application.
/// </summary>
/// <remarks>
/// <para>
/// Sets <c>NSApplicationActivationPolicyAccessory</c>, which takes the app out of
/// the Dock and out of Command-Tab while leaving it free to own a status item and
/// to open windows. It is what every menu bar utility does, and the right shape
/// for an app whose only durable surface is the menu bar and whose menu carries
/// its own Quit.
/// </para>
/// <para>
/// Done at runtime rather than through <c>LSUIElement</c> in an <c>Info.plist</c>,
/// which is the more usual route. The plist only applies to a packaged
/// <c>.app</c>, and this has to behave the same when run straight from a build
/// directory - which is exactly how it is tested. Setting the policy covers both,
/// and a bundle can still declare the key as well.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOsAppPresentation : IAppPresentation
{
    /// <summary>
    /// <c>NSApplicationActivationPolicyAccessory</c>: no Dock icon, windows allowed.
    /// </summary>
    /// <remarks>
    /// Not <c>Prohibited</c>, which is the other policy without a Dock icon. That
    /// one also refuses to activate the app, so the details popup could be shown
    /// but never focused - and this popup closes itself when it loses focus, so it
    /// would have shut the instant it opened.
    /// </remarks>
    private const long AccessoryPolicy = 1;

    /// <inheritdoc />
    public bool HideFromDock()
    {
        nint app = ObjC.Send(ObjC.Class("NSApplication"), "sharedApplication");
        if (app == 0)
        {
            return false;
        }

        ObjC.SendLong(app, ObjC.sel_registerName("setActivationPolicy:"), AccessoryPolicy);
        return true;
    }
}

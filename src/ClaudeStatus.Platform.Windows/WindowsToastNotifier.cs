using System.Runtime.Versioning;
using ClaudeStatus.Platform;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Real Windows toasts, through the WinRT notification API - no icon anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app id.</b> A toast is posted on behalf of an AppUserModelID, and
/// Windows shows it only when a Start Menu shortcut carries that id. Velopack's
/// installer writes one: <c>velopack.</c> followed by the pack id that
/// <c>build/pack.ps1</c> passes to <c>vpk pack</c>. Renaming the pack changes the
/// id, and this silently stops working - so the two must move together.
/// </para>
/// <para>
/// Windows does not check which executable posts the toast, only that the id is
/// registered. A development build therefore shows real toasts on any machine
/// where the app has also been installed, and nowhere else: there
/// <see cref="IsRegistered"/> is false and <see cref="WindowsNotifier"/> uses
/// the shell icon instead. Asking the toast API itself does not tell the two
/// apart - its <c>Setting</c> throws <c>0x80070490</c> for a registered id that
/// has not shown a toast yet, exactly as for one that does not exist.
/// </para>
/// <para>
/// <b>Clicks.</b> <see cref="ToastNotification.Activated"/> fires while this
/// process is alive, on a thread-pool thread; <see cref="Activated"/> is raised
/// there, synchronously, and the handler marshals. Once the app has exited, a
/// toast left in the notification centre has nothing to call back: that would
/// take a COM activator registered on the shortcut, which the installer does not
/// write. Each toast carries its own tag in its launch arguments, so a click on
/// an older one reports that one, not the newest.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsToastNotifier : INotifier
{
    /// <summary>The id Velopack's Start Menu shortcut carries. See the remarks.</summary>
    public const string AppUserModelId = "velopack.ClaudeStatus";

    /// <summary>The prefix of a toast's launch arguments, so a foreign or empty one is recognisable.</summary>
    private const string LaunchPrefix = "claudestatus-tag:";

    /// <summary>How many shown toasts are kept referenced, so their click handlers stay alive.</summary>
    private const int Remembered = 32;

    /// <summary>A toast's <c>Tag</c> is capped at 64 characters by the platform.</summary>
    private const int MaxToastTag = 64;

    private readonly Lock _gate = new();
    private readonly Queue<ToastNotification> _shown = new();
    private readonly ToastNotifier? _notifier;
    private bool _disposed;

    /// <summary>Creates a notifier for <see cref="AppUserModelId"/>.</summary>
    /// <remarks>Never throws; an unusable one reports <see cref="IsSupported"/> as false.</remarks>
    public WindowsToastNotifier()
        : this(AppUserModelId)
    {
    }

    /// <summary>Creates a notifier for an arbitrary app id. For tests.</summary>
    internal WindowsToastNotifier(string appUserModelId)
    {
        if (!AppUserModelIds.IsRegistered(appUserModelId))
        {
            return;
        }

        try
        {
            _notifier = ToastNotificationManager.CreateToastNotifier(appUserModelId);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException)
        {
            _notifier = null;
        }
    }

    /// <inheritdoc />
    public event EventHandler<NotificationActivatedEventArgs>? Activated;

    /// <inheritdoc />
    public bool IsSupported => _notifier is not null && !_disposed;

    /// <summary>Whether the app id this notifier posts under is registered on this machine.</summary>
    public bool IsRegistered => _notifier is not null;

    /// <inheritdoc />
    public bool Notify(string title, string message, string? tag = null)
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            var document = new XmlDocument();
            document.LoadXml(BuildXml(title, message, tag));

            var toast = new ToastNotification(document);
            if (tag is { Length: > 0 and <= MaxToastTag })
            {
                // One entry per session in the notification centre: a later toast
                // about the same session replaces the earlier one.
                toast.Tag = tag;
                toast.Group = "sessions";
            }

            toast.Activated += OnToastActivated;

            lock (_gate)
            {
                _shown.Enqueue(toast);
                while (_shown.Count > Remembered)
                {
                    _shown.Dequeue().Activated -= OnToastActivated;
                }
            }

            _notifier!.Show(toast);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_shown.Count > 0)
            {
                _shown.Dequeue().Activated -= OnToastActivated;
            }
        }
    }

    /// <summary>
    /// The toast's XML, with the text escaped by the DOM rather than by hand.
    /// </summary>
    /// <remarks>
    /// Title and message come from a session's folder name, which the user does
    /// not choose with XML in mind; built as a string they could inject elements.
    /// </remarks>
    internal static string BuildXml(string? title, string? message, string? tag)
    {
        var document = new System.Xml.XmlDocument();

        System.Xml.XmlElement toast = document.CreateElement("toast");
        toast.SetAttribute("launch", LaunchPrefix + (tag ?? string.Empty));
        document.AppendChild(toast);

        System.Xml.XmlElement visual = document.CreateElement("visual");
        toast.AppendChild(visual);

        System.Xml.XmlElement binding = document.CreateElement("binding");
        binding.SetAttribute("template", "ToastGeneric");
        visual.AppendChild(binding);

        foreach (string? line in new[] { title, message })
        {
            System.Xml.XmlElement text = document.CreateElement("text");
            text.InnerText = line ?? string.Empty;
            binding.AppendChild(text);
        }

        return document.OuterXml;
    }

    /// <summary>The tag a toast was shown with, from its launch arguments; null when it had none.</summary>
    internal static string? TagFromArguments(string? arguments)
        => arguments is not null
            && arguments.StartsWith(LaunchPrefix, StringComparison.Ordinal)
            && arguments.Length > LaunchPrefix.Length
                ? arguments[LaunchPrefix.Length..]
                : null;

    private void OnToastActivated(ToastNotification sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        string? tag = TagFromArguments((args as ToastActivatedEventArgs)?.Arguments);
        try
        {
            Activated?.Invoke(this, new NotificationActivatedEventArgs(tag));
        }
        catch (Exception)
        {
            // Raised from the platform's callback on a thread-pool thread, where an
            // unhandled exception ends the process. A click that could not be
            // followed is not worth that.
        }
    }
}

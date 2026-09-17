using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform.MacOS.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Real macOS notifications, through <c>UNUserNotificationCenter</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only inside a bundle.</b> <c>currentNotificationCenter</c> raises an
/// Objective-C exception in a process without a bundle identifier - which is what
/// <c>dotnet run</c> produces - and an Objective-C exception crossing into .NET
/// ends the process. So the bundle is checked first and nothing else is touched
/// without one; <see cref="Notify"/> then returns false and the caller shows its
/// card, as it did before this existed.
/// </para>
/// <para>
/// <b>Created before the app finishes launching.</b> Apple requires the center's
/// delegate to be set by then, or a click that launches the app is not delivered.
/// Avalonia's native backend finishes launching inside <c>[NSApp run]</c>, which
/// starts only after <c>OnFrameworkInitializationCompleted</c> returns, so the app
/// resolves this there. The controller subscribes to <see cref="Activated"/> much
/// later, after loading its settings, so a click that arrives first is held and
/// handed to the first subscriber.
/// </para>
/// <para>
/// <b>Never waits for the user.</b> Permission is requested once, at construction;
/// the first run shows the system prompt, and until it is answered every
/// <see cref="Notify"/> reports false and the card is shown. Each post re-reads the
/// current setting, because the user can turn notifications off in System Settings
/// at any time. Both that read and the post itself complete on a background queue,
/// and are waited for briefly so that a failure still falls back to the card.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacNotifier : INotifier
{
    /// <summary>The <c>userInfo</c> key the tag travels under.</summary>
    internal const string TagKey = "claudeStatusTag";

    /// <summary>How long a settings read may take before the card is shown instead.</summary>
    private static readonly TimeSpan SettingsTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How long a post may take before the card is shown instead.</summary>
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(2);

    // UNAuthorizationOptions.
    private const nint OptionSound = 1 << 1;
    private const nint OptionAlert = 1 << 2;

    // UNNotificationPresentationOptions: sound, list and banner.
    private const nint PresentWhileFrontmost = (1 << 1) | (1 << 3) | (1 << 4);

    // UNAuthorizationStatus values that allow a post: authorized, provisional, ephemeral.
    private const long StatusAuthorized = 2;
    private const long StatusProvisional = 3;
    private const long StatusEphemeral = 4;

    /// <summary>
    /// The live notifier, for the delegate's static callbacks to find their way home.
    /// </summary>
    /// <remarks>
    /// One per process, for the reason <see cref="MacOsStatusItem"/> has one: an
    /// <c>UnmanagedCallersOnly</c> method closes over nothing, and there is exactly
    /// one notification center to be the delegate of.
    /// </remarks>
    private static MacNotifier? _current;

    private static nint _delegateClass;

    /// <summary>
    /// Blocks whose callback has run, freed at the next opportunity.
    /// </summary>
    /// <remarks>
    /// Not freed inside the callback: the runtime still reads the block after the
    /// invoke returns. See <see cref="ObjCBlock"/>.
    /// </remarks>
    private static readonly ConcurrentQueue<nint> FinishedBlocks = new();

    private readonly ILogger<MacNotifier> _log;
    private readonly nint _center;
    private readonly nint _delegate;
    private readonly Lock _gate = new();

    private EventHandler<NotificationActivatedEventArgs>? _activated;
    private (bool Waiting, string? Tag) _heldClick;
    private long? _lastStatus;
    private bool _disposed;

    /// <param name="log">Reports the permission answer and any post that failed.</param>
    public MacNotifier(ILogger<MacNotifier>? log = null)
    {
        _log = log ?? NullLogger<MacNotifier>.Instance;

        string? bundle = BundleIdentifier();
        if (bundle is null)
        {
            _log.LogInformation("No bundle identifier: notifications are off and the card is used instead.");
            return;
        }

        NativeLibrary.TryLoad("/System/Library/Frameworks/UserNotifications.framework/UserNotifications", out _);
        nint centerClass = ObjC.Class("UNUserNotificationCenter");
        if (centerClass == 0)
        {
            _log.LogWarning("UserNotifications could not be loaded: notifications are off.");
            return;
        }

        _center = ObjC.Send(centerClass, "currentNotificationCenter");
        _delegate = CreateDelegate();
        _current = this;
        ObjC.Send(_center, "setDelegate:", _delegate);

        // Whether this ran in time is otherwise invisible: a click that launched the
        // app just never arrives.
        _log.LogInformation(
            "Notification delegate set for {Bundle}. Launch already finished: {Running}.",
            bundle,
            IsAppRunning());

        RequestAuthorization();
    }

    /// <inheritdoc />
    /// <remarks>
    /// True inside a bundle, where the notification center exists. Whether the user
    /// allows notifications is a separate question, asked at every post.
    /// </remarks>
    public bool IsSupported => _center != 0;

    /// <inheritdoc />
    /// <remarks>
    /// Delivered on the main thread. A click that arrives before anything has
    /// subscribed - the one that launched the app, typically - is held and raised to
    /// the first subscriber, once.
    /// </remarks>
    public event EventHandler<NotificationActivatedEventArgs>? Activated
    {
        add
        {
            (bool Waiting, string? Tag) held;
            lock (_gate)
            {
                _activated += value;
                held = _heldClick;
                _heldClick = default;
            }

            if (held.Waiting)
            {
                value?.Invoke(this, new NotificationActivatedEventArgs(held.Tag));
            }
        }

        remove
        {
            lock (_gate)
            {
                _activated -= value;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The tag is also the request identifier, so a session's newer notification
    /// replaces its older one in Notification Center instead of piling up.
    /// </remarks>
    public bool Notify(string title, string message, string? tag = null)
    {
        if (_disposed || !IsSupported)
        {
            return false;
        }

        FreeFinishedBlocks();

        try
        {
            long? status = ReadAuthorizationStatus();
            if (status != _lastStatus)
            {
                _lastStatus = status;
                _log.LogInformation("Notification permission is now {Status}.", DescribeStatus(status));
            }

            if (status is not (StatusAuthorized or StatusProvisional or StatusEphemeral))
            {
                return false;
            }

            string? error = Post(title, message, tag);
            if (error is not null)
            {
                _log.LogWarning("A notification could not be posted: {Error}", error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Never throws, per the interface: the card is a perfectly good answer.
            _log.LogError(ex, "A notification could not be posted.");
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_center != 0 && _current == this)
        {
            ObjC.Send(_center, "setDelegate:", 0);
            _current = null;
            ObjC.Send(_delegate, "release");
        }

        FreeFinishedBlocks();
    }

    /// <summary>Raises <see cref="Activated"/>, or holds the click until someone listens.</summary>
    internal void RaiseActivated(string? tag)
    {
        EventHandler<NotificationActivatedEventArgs>? handler;
        lock (_gate)
        {
            handler = _activated;
            if (handler is null)
            {
                _heldClick = (true, tag);
                return;
            }
        }

        handler(this, new NotificationActivatedEventArgs(tag));
    }

    /// <summary>
    /// A completion block for <c>addNotificationRequest:withCompletionHandler:</c>,
    /// and the task its answer arrives on: null for success, a description otherwise.
    /// </summary>
    internal static (nint Block, Task<string?> Result) CreatePostCompletion()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint block = ObjCBlock.Create(
            (nint)(delegate* unmanaged<nint, nint, void>)&OnPosted,
            GCHandle.ToIntPtr(GCHandle.Alloc(completion)));

        return (block, completion.Task);
    }

    /// <summary>The running bundle's identifier, or null outside a bundle.</summary>
    private static string? BundleIdentifier()
    {
        NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
        nint bundleClass = ObjC.Class("NSBundle");
        if (bundleClass == 0)
        {
            return null;
        }

        nint bundle = ObjC.Send(bundleClass, "mainBundle");
        return bundle == 0 ? null : ObjC.ManagedString(ObjC.Send(bundle, "bundleIdentifier"));
    }

    private static bool IsAppRunning()
    {
        nint appClass = ObjC.Class("NSApplication");
        return appClass != 0
            && (ObjC.SendReturningLong(ObjC.Send(appClass, "sharedApplication"), ObjC.sel_registerName("isRunning")) & 0xFF) != 0;
    }

    private static string DescribeStatus(long? status) => status switch
    {
        null => "unknown (no answer in time)",
        0 => "not determined",
        1 => "denied",
        StatusAuthorized => "authorized",
        StatusProvisional => "provisional",
        StatusEphemeral => "ephemeral",
        _ => $"status {status}",
    };

    private static void FreeFinishedBlocks()
    {
        while (FinishedBlocks.TryDequeue(out nint block))
        {
            ObjCBlock.Free(block);
        }
    }

    /// <summary>Takes the context a completion block was built with, and releases its handle.</summary>
    private static T TakeContext<T>(nint block)
    {
        GCHandle handle = GCHandle.FromIntPtr(ObjCBlock.ContextOf(block));
        var value = (T)handle.Target!;
        handle.Free();
        return value;
    }

    /// <summary>Asks once. The system shows its prompt only the first time.</summary>
    private void RequestAuthorization()
    {
        nint block = ObjCBlock.Create((nint)(delegate* unmanaged<nint, byte, nint, void>)&OnAuthorized, 0);
        ObjC.Send(
            _center,
            ObjC.sel_registerName("requestAuthorizationWithOptions:completionHandler:"),
            OptionAlert | OptionSound,
            block);
    }

    /// <summary>The current <c>UNAuthorizationStatus</c>, or null when it did not arrive in time.</summary>
    private long? ReadAuthorizationStatus()
    {
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint block = ObjCBlock.Create(
            (nint)(delegate* unmanaged<nint, nint, void>)&OnSettings,
            GCHandle.ToIntPtr(GCHandle.Alloc(completion)));

        ObjC.Send(_center, "getNotificationSettingsWithCompletionHandler:", block);

        return completion.Task.Wait(SettingsTimeout) ? completion.Task.Result : null;
    }

    /// <summary>Posts one notification. Returns null on success, or why it failed.</summary>
    private string? Post(string title, string message, string? tag)
    {
        (nint block, Task<string?> result) = CreatePostCompletion();

        nint pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            nint content = ObjC.Send(ObjC.Send(ObjC.Class("UNMutableNotificationContent"), "alloc"), "init");
            ObjC.Send(content, "setTitle:", ObjC.NSString(title));
            ObjC.Send(content, "setBody:", ObjC.NSString(message));
            ObjC.Send(content, "setSound:", ObjC.Send(ObjC.Class("UNNotificationSound"), "defaultSound"));

            if (tag is not null)
            {
                ObjC.Send(
                    content,
                    "setUserInfo:",
                    ObjC.Send(
                        ObjC.Class("NSDictionary"),
                        ObjC.sel_registerName("dictionaryWithObject:forKey:"),
                        ObjC.NSString(tag),
                        ObjC.NSString(TagKey)));
            }

            nint request = ObjC.Send(
                ObjC.Class("UNNotificationRequest"),
                ObjC.sel_registerName("requestWithIdentifier:content:trigger:"),
                ObjC.NSString(tag ?? Guid.NewGuid().ToString("N")),
                content,
                0);

            ObjC.Send(_center, ObjC.sel_registerName("addNotificationRequest:withCompletionHandler:"), request, block);
            ObjC.Send(content, "release");
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }

        return result.Wait(PostTimeout) ? result.Result : "no answer in time";
    }

    /// <summary>Builds the delegate object, defining its class the first time.</summary>
    private static nint CreateDelegate()
    {
        if (_delegateClass == 0)
        {
            const string Name = "ClaudeStatusNotificationDelegate";
            _delegateClass = ObjC.objc_getClass(Name);

            if (_delegateClass == 0)
            {
                _delegateClass = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), Name, 0);

                // void, self, selector, center, response or notification, block.
                ObjC.class_addMethod(
                    _delegateClass,
                    ObjC.sel_registerName("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                    (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&OnResponse,
                    "v@:@@@?");

                ObjC.class_addMethod(
                    _delegateClass,
                    ObjC.sel_registerName("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                    (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&OnWillPresent,
                    "v@:@@@?");

                if (ObjC.objc_getProtocol("UNUserNotificationCenterDelegate") is var protocol and not 0)
                {
                    ObjC.class_addProtocol(_delegateClass, protocol);
                }

                ObjC.objc_registerClassPair(_delegateClass);
            }
        }

        return ObjC.Send(ObjC.Send(_delegateClass, "alloc"), "init");
    }

    /// <summary>A notification was clicked.</summary>
    /// <remarks>
    /// Nothing may escape from here: this frame is called by Objective-C. The
    /// completion handler is called whatever happens, as the API requires.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void OnResponse(nint self, nint selector, nint center, nint response, nint completionHandler)
    {
        try
        {
            nint content = ObjC.Send(ObjC.Send(ObjC.Send(response, "notification"), "request"), "content");
            nint userInfo = ObjC.Send(content, "userInfo");
            string? tag = userInfo == 0
                ? null
                : ObjC.ManagedString(ObjC.Send(userInfo, "objectForKey:", ObjC.NSString(TagKey)));

            _current?.RaiseActivated(tag);
        }
        catch (Exception ex)
        {
            _current?._log.LogError(ex, "A notification click could not be handled.");
        }
        finally
        {
            if (completionHandler != 0)
            {
                ObjCBlock.Invoke(completionHandler);
            }
        }
    }

    /// <summary>
    /// A notification arrived while this app is frontmost - the popup open, say.
    /// </summary>
    /// <remarks>
    /// Without an answer the system shows nothing at all in that case.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void OnWillPresent(nint self, nint selector, nint center, nint notification, nint completionHandler)
    {
        if (completionHandler != 0)
        {
            ObjCBlock.Invoke(completionHandler, PresentWhileFrontmost);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnAuthorized(nint block, byte granted, nint error)
    {
        try
        {
            string? why = error == 0 ? null : ObjC.ManagedString(ObjC.Send(error, "localizedDescription"));
            _current?._log.LogInformation(
                "Notification permission answered: granted={Granted}{Error}.",
                granted != 0,
                why is null ? string.Empty : $" error=\"{why}\"");
        }
        catch (Exception ex)
        {
            _current?._log.LogError(ex, "The notification permission answer could not be read.");
        }
        finally
        {
            FinishedBlocks.Enqueue(block);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnSettings(nint block, nint settings)
    {
        try
        {
            TaskCompletionSource<long> completion = TakeContext<TaskCompletionSource<long>>(block);
            completion.TrySetResult(settings == 0
                ? -1
                : ObjC.SendReturningLong(settings, ObjC.sel_registerName("authorizationStatus")));
        }
        catch (Exception ex)
        {
            _current?._log.LogError(ex, "The notification settings could not be read.");
        }
        finally
        {
            FinishedBlocks.Enqueue(block);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnPosted(nint block, nint error)
    {
        try
        {
            TaskCompletionSource<string?> completion = TakeContext<TaskCompletionSource<string?>>(block);
            completion.TrySetResult(error == 0
                ? null
                : ObjC.ManagedString(ObjC.Send(error, "localizedDescription")) ?? "unknown error");
        }
        catch (Exception ex)
        {
            _current?._log.LogError(ex, "A notification post result could not be read.");
        }
        finally
        {
            FinishedBlocks.Enqueue(block);
        }
    }
}

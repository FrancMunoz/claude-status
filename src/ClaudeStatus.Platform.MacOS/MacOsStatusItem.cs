using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeStatus.Platform.MacOS.Interop;
using ClaudeStatus.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// A macOS menu bar item owned directly, rather than through Avalonia.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Avalonia's <c>TrayIcon</c> creates its status item with
/// <c>NSSquareStatusItemLength</c> - the constant is compiled into
/// <c>libAvaloniaNative.dylib</c> and there is no property that changes it - so the
/// item is exactly as wide as the menu bar is tall and anything wider than a square
/// is clipped. A row of percentages cannot fit in a square.
/// </para>
/// <para>
/// The second reason is the click. Avalonia sets the item's <c>menu</c>, and AppKit
/// gives a status item with a menu no action at all: it opens the menu on every
/// button, so a left click could never mean "show me the details". Leaving
/// <c>menu</c> unset and popping it up by hand on the right button is the only way
/// to have both.
/// </para>
/// <para>
/// Text rather than a rendered bitmap, which is the third thing it buys. An
/// <c>NSStatusItem</c> with a variable length sizes itself to its title, so the row
/// is measured by AppKit in the menu bar's own font, and the colour follows the
/// menu bar through dark mode, light mode and the highlight while the menu is open,
/// with none of it drawn by us.
/// </para>
/// <para>
/// Every method here must run on the main thread. AppKit is not thread-safe and the
/// caller - the indicator - is already on Avalonia's UI thread, which on macOS is
/// the main thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOsStatusItem : INativeStatusItem
{
    /// <summary>Sizes the item to its content, rather than to a square.</summary>
    /// <remarks>This is the constant Avalonia hardcodes to -2, and the whole reason
    /// this class exists.</remarks>
    private const double VariableLength = -1.0;

    /// <summary><c>NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp</c>.</summary>
    /// <remarks>
    /// A button reports only <c>NSEventMaskLeftMouseUp</c> by default, so without
    /// this the right button would never reach the action at all.
    /// </remarks>
    private const long LeftAndRightMouseUp = (1L << 2) | (1L << 4);

    private const long NSEventTypeRightMouseUp = 4;
    private const long NSEventModifierFlagControl = 1L << 18;

    private const long NSControlStateValueOn = 1;
    private const long NSControlStateValueOff = 0;

    /// <summary><c>NSImageLeft</c>: the mark sits before the text.</summary>
    private const long NSImageLeft = 2;

    /// <summary>
    /// The mark's size in points, beside menu bar text.
    /// </summary>
    /// <remarks>
    /// The menu bar is 22 pt and its own icons leave a margin rather than filling
    /// it. Sixteen matches them and keeps the mark from overpowering the number,
    /// which is the thing being read.
    /// </remarks>
    private const double IconPoints = 16d;

    /// <summary>The action every button and menu item routes through.</summary>
    private const string ActionSelector = "claudeStatusAction:";

    /// <summary>
    /// The single live item, for the static callback to find its way home.
    /// </summary>
    /// <remarks>
    /// An <c>UnmanagedCallersOnly</c> method cannot close over anything, and a
    /// menu bar app has exactly one status item, so a static field is the honest
    /// shape rather than a table keyed by a pointer that would only ever hold one
    /// row.
    /// </remarks>
    private static MacOsStatusItem? _current;

    private static nint _targetClass;

    private readonly nint _statusItem;
    private readonly nint _target;
    private readonly ILogger<MacOsStatusItem> _log;
    private nint _menu;
    private bool _disposed;

    /// <summary>Set while the startup self-test is invoking the action by hand.</summary>
    /// <remarks>
    /// The self-test proves the managed callback can be reached through the
    /// Objective-C runtime at all. Without it, "the action never ran" has two
    /// completely different causes - a class that was built wrong, or an event
    /// AppKit never delivered - and no way to tell them apart from a log.
    /// </remarks>
    private bool _selfTesting;

    private bool _selfTestReachedManagedCode;

    /// <summary>Whether a click has ever arrived.</summary>
    /// <remarks>
    /// The first one is reported at information level and the rest at debug. That
    /// one line is the difference between "the wiring works" and "the action never
    /// fires", which is the only question worth asking of interop like this - and
    /// asking it once costs nothing, where a line per click would fill the log.
    /// </remarks>
    private bool _sawClick;

    /// <param name="log">
    /// Where the wiring reports itself. Interop of this kind fails silently by
    /// nature - a selector that does not exist is a no-op, not an error - so the
    /// setup states what it managed to connect rather than leaving a dead menu bar
    /// item as the only symptom.
    /// </param>
    public MacOsStatusItem(ILogger<MacOsStatusItem>? log = null)
    {
        _log = log ?? NullLogger<MacOsStatusItem>.Instance;
        nint statusBar = ObjC.Send(ObjC.Class("NSStatusBar"), "systemStatusBar");
        _statusItem = ObjC.SendDouble(
            statusBar, ObjC.sel_registerName("statusItemWithLength:"), VariableLength);

        // The status bar owns it, but nothing else here does, and a status item
        // that is released disappears from the menu bar.
        ObjC.Send(_statusItem, "retain");

        _target = CreateTarget();
        _current = this;

        nint button = Button;
        nint action = ObjC.sel_registerName(ActionSelector);

        if (button != 0)
        {
            ObjC.Send(button, ObjC.sel_registerName("setTarget:"), _target);
            ObjC.Send(button, ObjC.sel_registerName("setAction:"), action);
            ObjC.SendLong(button, ObjC.sel_registerName("sendActionOn:"), LeftAndRightMouseUp);
        }

        // Read back rather than assume. Each of these is a separate way for the
        // click to be lost, and all of them look identical from the outside.
        _log.LogInformation(
            "Menu bar item wired. item={Item} button={Button} target={Target} "
            + "responds={Responds} action={Action} targetStuck={TargetStuck} "
            + "enabled={Enabled} window={Window}",
            _statusItem != 0,
            button != 0,
            _target != 0,
            _target != 0 && ObjC.SendPointer(
                _target, ObjC.sel_registerName("respondsToSelector:"), action) != 0,
            button != 0 && ObjC.Send(button, "action") == action,

            // The one that a readback of "action" cannot catch. NSControl.target is
            // a weak reference: if nothing retains the object it is zeroed, the
            // action goes up the responder chain instead, and nothing answers it.
            button != 0 && ObjC.Send(button, "target") == _target,
            button != 0 && ObjC.Send(button, "isEnabled") != 0,
            button != 0 && ObjC.Send(button, "window") != 0);

        SelfTest(action, button);
    }

    /// <summary>
    /// Invokes the action through the runtime, the way AppKit would.
    /// </summary>
    /// <remarks>
    /// Sends the selector straight to the target rather than clicking the button,
    /// so it exercises the class, the method and the managed function pointer
    /// without involving event delivery. If this reports true and a real click
    /// still does nothing, the fault is in how the event reaches the button, not in
    /// any of the machinery above.
    /// </remarks>
    private void SelfTest(nint action, nint button)
    {
        if (_target == 0 || action == 0)
        {
            return;
        }

        _selfTesting = true;
        try
        {
            ObjC.Send(_target, action, button);
        }
        finally
        {
            _selfTesting = false;
        }

        _log.LogInformation(
            "Menu bar action callback self-test reached managed code: {Reached}",
            _selfTestReachedManagedCode);
    }

    /// <summary>Raised on a left click, which is the gesture that means "show me".</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Raised with the tag of the menu entry the user picked.</summary>
    public event EventHandler<long>? MenuItemClicked;

    /// <summary>The item's button, which is what carries the title and the click.</summary>
    private nint Button => ObjC.Send(_statusItem, "button");

    /// <summary>Whether a menu bar item could be created at all.</summary>
    public bool IsAvailable => _statusItem != 0;

    /// <summary>
    /// Sets the text shown in the menu bar.
    /// </summary>
    /// <param name="text">The whole row, already composed and localised.</param>
    /// <param name="tint">How to colour it.</param>
    public void SetTitle(string text, StatusTint tint)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed)
        {
            return;
        }

        nint button = Button;
        if (button == 0)
        {
            return;
        }

        // A plain title, never an attributed one.
        //
        // The colour of menu bar text is not a colour this app can name. It was set
        // to labelColor, which resolves against the *application's* appearance - and
        // the menu bar turns dark whenever the desktop picture behind it is dark,
        // Light mode included, so labelColor came out black on a bar that was not.
        // NSStatusBarButton already knows the right answer for its own context, and
        // handing it a bare string is how to get it. The menu bar font arrives the
        // same way, which is the other thing the attributed string was there for.
        ObjC.Send(button, ObjC.sel_registerName("setTitle:"), ObjC.NSString(text));

        // Red is the one colour worth overriding for: it means the same thing in
        // every appearance and has to survive being read at a glance. It tints the
        // template mark too, so the two stay together.
        ObjC.Send(button, ObjC.sel_registerName("setContentTintColor:"), AlertColour(tint));

        // Fading is opacity, not a colour. It dims the mark and the number together
        // and keeps whatever colour the menu bar picked, which is the whole point.
        ObjC.SendDouble(
            button,
            ObjC.sel_registerName("setAlphaValue:"),
            tint == StatusTint.Stale ? IndicatorText.StaleAlpha : 1d);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Marked as a template image, which discards the colours and keeps the alpha
    /// so the menu bar can tint the mark itself - that is what makes it follow dark
    /// and light mode. <c>contentTintColor</c>, set alongside the title, then takes
    /// it the rest of the way so the mark matches the text even when the text is
    /// red or faded.
    /// </remarks>
    public void SetIcon(ReadOnlySpan<byte> png)
    {
        if (_disposed || png.IsEmpty)
        {
            return;
        }

        nint button = Button;
        if (button == 0)
        {
            return;
        }

        nint data;
        unsafe
        {
            fixed (byte* bytes = png)
            {
                data = ObjC.SendBytes(
                    ObjC.Class("NSData"),
                    ObjC.sel_registerName("dataWithBytes:length:"),
                    (nint)bytes,
                    png.Length);
            }
        }

        if (data == 0)
        {
            return;
        }

        nint image = ObjC.Send(
            ObjC.Send(ObjC.Class("NSImage"), "alloc"),
            ObjC.sel_registerName("initWithData:"),
            data);

        if (image == 0)
        {
            return;
        }

        ObjC.SendBool(image, ObjC.sel_registerName("setTemplate:"), true);

        // The bitmap arrives at its pixel size, which on a HiDPI display is twice
        // what it should occupy. Naming the point size is what turns those extra
        // pixels into sharpness rather than into a mark twice too big.
        ObjC.SendSize(
            image,
            ObjC.sel_registerName("setSize:"),
            new ObjC.CGSize(IconPoints, IconPoints));

        ObjC.Send(button, ObjC.sel_registerName("setImage:"), image);
        ObjC.SendLong(button, ObjC.sel_registerName("setImagePosition:"), NSImageLeft);
        ObjC.Send(image, "release");
    }

    /// <summary>Replaces the menu shown on a right click.</summary>
    /// <remarks>
    /// Built fresh each time rather than mutated. An <c>NSMenu</c> is cheap, this
    /// runs on a language change rather than on a poll, and rebuilding sidesteps
    /// the ownership question of an item that is already in a menu.
    /// </remarks>
    public void SetMenu(IReadOnlyList<StatusMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (_disposed)
        {
            return;
        }

        nint replacement = BuildMenu(entries);

        if (_menu != 0)
        {
            ObjC.Send(_menu, "release");
        }

        _menu = replacement;
    }

    /// <summary>Shows or hides the item.</summary>
    public void SetVisible(bool visible)
    {
        if (!_disposed)
        {
            ObjC.SendBool(_statusItem, ObjC.sel_registerName("setVisible:"), visible);
        }
    }

    /// <summary>
    /// Pops the menu up under the item, the way a right click should.
    /// </summary>
    /// <remarks>
    /// <c>popUpContextMenu:withEvent:forView:</c> rather than the more common trick
    /// of assigning the item's <c>menu</c> and calling <c>performClick:</c>. That
    /// trick re-enters the button's own action while this method is running inside
    /// it, and assigning <c>menu</c> at all - even briefly - is what gives AppKit
    /// every click, which is the behaviour this class exists to avoid.
    /// </remarks>
    private void PopUpMenu(nint mouseEvent)
    {
        nint button = Button;
        if (_menu == 0 || button == 0)
        {
            return;
        }

        ObjC.Send(
            ObjC.Class("NSMenu"),
            ObjC.sel_registerName("popUpContextMenu:withEvent:forView:"),
            _menu,
            mouseEvent,
            button);
    }

    /// <summary>Builds an <c>NSMenu</c> from the description.</summary>
    private nint BuildMenu(IReadOnlyList<StatusMenuEntry> entries)
    {
        nint menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), "alloc"), "init");

        // Without this AppKit asks a validator whether each item is enabled, and
        // with no responder to ask it disables the lot.
        ObjC.SendBool(menu, ObjC.sel_registerName("setAutoenablesItems:"), false);

        foreach (StatusMenuEntry entry in entries)
        {
            ObjC.Send(menu, ObjC.sel_registerName("addItem:"), BuildItem(entry));
        }

        return menu;
    }

    private nint BuildItem(StatusMenuEntry entry)
    {
        if (entry.IsSeparator)
        {
            return ObjC.Send(ObjC.Class("NSMenuItem"), "separatorItem");
        }

        // A parent must not carry an action of its own: clicking it opens the
        // submenu, and an action there would fire a command the user never chose.
        nint action = entry.Submenu is null ? ObjC.sel_registerName(ActionSelector) : 0;

        nint item = ObjC.Send(
            ObjC.Send(ObjC.Class("NSMenuItem"), "alloc"),
            ObjC.sel_registerName("initWithTitle:action:keyEquivalent:"),
            ObjC.NSString(entry.Title!),
            action,
            ObjC.NSString(string.Empty));

        ObjC.SendLong(item, ObjC.sel_registerName("setTag:"), entry.Tag);
        ObjC.SendLong(
            item,
            ObjC.sel_registerName("setState:"),
            entry.IsChecked ? NSControlStateValueOn : NSControlStateValueOff);

        if (entry.Submenu is { } children)
        {
            nint submenu = BuildMenu(children);
            ObjC.Send(item, ObjC.sel_registerName("setSubmenu:"), submenu);

            // The item retains it; this balances the alloc in BuildMenu.
            ObjC.Send(submenu, "release");
        }
        else
        {
            ObjC.Send(item, ObjC.sel_registerName("setTarget:"), _target);
        }

        // addItem: retains, so the alloc above is balanced by handing it over.
        ObjC.Send(item, "autorelease");
        return item;
    }

    /// <summary>The override colour for a tint, or nil to leave it to the menu bar.</summary>
    /// <remarks>
    /// Nil is the good case and the common one. Naming a colour here is a claim to
    /// know what the menu bar is drawing, and only the alert red earns that: every
    /// other state is better served by the bar's own choice, dimmed where needed.
    /// </remarks>
    private static nint AlertColour(StatusTint tint)
        => tint == StatusTint.Alert
            ? ObjC.Send(ObjC.Class("NSColor"), "systemRedColor")
            : 0;

    /// <summary>
    /// Builds the Objective-C class whose one method is the action callback.
    /// </summary>
    /// <remarks>
    /// A target/action pair needs a real Objective-C object to send to, and there
    /// is no existing class with a method that calls back into managed code - so
    /// one is defined at runtime with an <c>UnmanagedCallersOnly</c> function
    /// pointer as its implementation. Registered once per process: allocating a
    /// class pair whose name is taken returns null.
    /// </remarks>
    private static unsafe nint CreateTarget()
    {
        if (_targetClass == 0)
        {
            const string Name = "ClaudeStatusActionTarget";
            _targetClass = ObjC.objc_getClass(Name);

            if (_targetClass == 0)
            {
                _targetClass = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), Name, 0);

                ObjC.class_addMethod(
                    _targetClass,
                    ObjC.sel_registerName(ActionSelector),
                    (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnAction,

                    // void, self, selector, one object argument.
                    "v@:@");

                ObjC.objc_registerClassPair(_targetClass);
            }
        }

        return ObjC.Send(ObjC.Send(_targetClass, "alloc"), "init");
    }

    /// <summary>
    /// The action callback, invoked by AppKit for the button and every menu item.
    /// </summary>
    /// <remarks>
    /// Nothing may escape from here. This frame is called from Objective-C, which
    /// has no idea what a managed exception is, and letting one cross the boundary
    /// tears the process down with no stack worth reading.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void OnAction(nint self, nint selector, nint sender)
    {
        MacOsStatusItem? item = _current;

        item?._log.LogInformation(
            "Menu bar action fired. sender={Sender} current={Current} selfTest={SelfTest}",
            sender,
            item is not null,
            item?._selfTesting);

        if (item is { _selfTesting: true })
        {
            item._selfTestReachedManagedCode = true;
            return;
        }

        try
        {
            item?.Dispatch(sender);
        }
        catch (Exception ex)
        {
            // A click that does nothing is recoverable; a crash is not. It is still
            // worth saying so - a silently swallowed click is indistinguishable
            // from one that never arrived.
            item?._log.LogError(ex, "A menu bar click could not be handled.");
        }
    }

    /// <summary>Routes one click to the right event.</summary>
    private void Dispatch(nint sender)
    {
        // Identified by identity, never by tag.
        //
        // This read "a tag of zero means the button", which is wrong in a way that
        // is invisible until something is clicked: an NSStatusBarButton reports a
        // tag of -1, so every click was routed into the menu handler, decoded to a
        // command that does not exist, and dropped without a sound. Comparing the
        // sender to the button asks the question that was actually meant.
        nint button = Button;
        _log.LogInformation(
            "Menu bar dispatch. sender={Sender} button={Button} match={Match}",
            sender,
            button,
            sender == button);

        if (sender != 0 && sender == button)
        {
            DispatchButtonClick();
            return;
        }

        long tag = ObjC.SendReturningLong(sender, ObjC.sel_registerName("tag"));

        if (FirstClick())
        {
            _log.LogInformation("Menu bar menu item {Tag} chosen.", tag);
        }
        else
        {
            _log.LogDebug("Menu bar menu item {Tag} chosen.", tag);
        }

        MenuItemClicked?.Invoke(this, tag);
    }

    /// <summary>Handles a click on the item itself, left or right.</summary>
    private void DispatchButtonClick()
    {
        nint mouseEvent = CurrentEvent();
        bool secondary = IsSecondaryClick(mouseEvent);
        _log.LogInformation("Menu bar item clicked. secondary={Secondary}", secondary);

        if (secondary)
        {
            PopUpMenu(mouseEvent);
            return;
        }

        LeftClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether this is the first click, which is the one worth announcing.</summary>
    private bool FirstClick()
    {
        if (_sawClick)
        {
            return false;
        }

        _sawClick = true;
        return true;
    }

    /// <summary>The event being handled, or zero when there is none.</summary>
    private static nint CurrentEvent()
    {
        nint app = ObjC.Send(ObjC.Class("NSApplication"), "sharedApplication");
        return app == 0 ? 0 : ObjC.Send(app, "currentEvent");
    }

    /// <summary>Whether the click that is being handled asked for the menu.</summary>
    /// <remarks>
    /// Control-click counts. It is the long-standing way to reach a context menu on
    /// a one-button mouse, and macOS still treats it as equivalent everywhere else.
    /// </remarks>
    private static bool IsSecondaryClick(nint currentEvent)
    {
        if (currentEvent == 0)
        {
            return false;
        }

        long type = ObjC.SendReturningLong(currentEvent, ObjC.sel_registerName("type"));
        if (type == NSEventTypeRightMouseUp)
        {
            return true;
        }

        long modifiers = ObjC.SendReturningLong(
            currentEvent, ObjC.sel_registerName("modifierFlags"));

        return (modifiers & NSEventModifierFlagControl) != 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_menu != 0)
        {
            ObjC.Send(_menu, "release");
            _menu = 0;
        }

        if (_statusItem != 0)
        {
            // Removing it is what makes the icon go away; releasing alone leaves a
            // gap in the menu bar until the status bar notices.
            nint statusBar = ObjC.Send(ObjC.Class("NSStatusBar"), "systemStatusBar");
            ObjC.Send(statusBar, ObjC.sel_registerName("removeStatusItem:"), _statusItem);
            ObjC.Send(_statusItem, "release");
        }

        if (_target != 0)
        {
            ObjC.Send(_target, "release");
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }
}

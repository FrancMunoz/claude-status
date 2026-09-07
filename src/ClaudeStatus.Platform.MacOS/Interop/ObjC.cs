using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeStatus.Platform.MacOS.Interop;

/// <summary>
/// The slice of the Objective-C runtime needed to own an <c>NSStatusItem</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than taken from a binding library because the surface is
/// tiny and a dependency that wraps all of AppKit would be far larger than the
/// dozen selectors used here.
/// </para>
/// <para>
/// <b>Every entry point is a separate declaration on purpose.</b> <c>objc_msgSend</c>
/// is variadic in C and must be cast to the callee's real signature before being
/// called - the arguments are placed in registers according to that signature, so
/// one <c>IntPtr</c>-shaped declaration reused for a call taking a <c>double</c>
/// puts the value in an integer register the callee never reads. The separate
/// names below are those casts.
/// </para>
/// <para>
/// Only the pointer- and scalar-returning forms are declared. A method returning a
/// struct needs <c>objc_msgSend_stret</c> on x86_64 and plain <c>objc_msgSend</c>
/// on arm64, and nothing here needs one - keeping it that way avoids a difference
/// that only shows up on one architecture.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    /// <summary>Looks a class up by name, or zero when it is not loaded.</summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_getClass(string name);

    /// <summary>Interns a selector.</summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint sel_registerName(string name);

    /// <summary>Starts a new class. Returns zero when the name is already taken.</summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    /// <summary>Makes a class built by <see cref="objc_allocateClassPair"/> usable.</summary>
    [LibraryImport(Runtime)]
    internal static partial void objc_registerClassPair(nint cls);

    /// <summary>
    /// Adds a method to a class.
    /// </summary>
    /// <param name="types">
    /// The Objective-C type encoding of the signature - <c>"v@:@"</c> is
    /// "returns void, takes self, selector and one object", which is the shape of
    /// every action method here.
    /// </param>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    /// <summary>For selectors taking a <c>CGFloat</c>, such as <c>statusItemWithLength:</c>.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendDouble(nint receiver, nint selector, double arg1);

    /// <summary>For selectors taking an <c>NSInteger</c> or an <c>NSUInteger</c>.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendLong(nint receiver, nint selector, long arg1);

    /// <summary>For selectors taking a <c>BOOL</c>.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool arg1);

    /// <summary>For <c>respondsToSelector:</c> and friends, whose argument is a SEL.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendPointer(nint receiver, nint selector, nint arg1);

    /// <summary>For selectors returning an <c>NSInteger</c>.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial long SendReturningLong(nint receiver, nint selector);

    /// <summary>Sends a message by selector name, for the one-off calls.</summary>
    internal static nint Send(nint receiver, string selector)
        => Send(receiver, sel_registerName(selector));

    /// <summary>Sends a one-argument message by selector name.</summary>
    internal static nint Send(nint receiver, string selector, nint arg1)
        => Send(receiver, sel_registerName(selector), arg1);

    /// <summary>The shared class object for a name.</summary>
    internal static nint Class(string name) => objc_getClass(name);

    /// <summary>For selectors taking an <c>NSSize</c>, such as <c>setSize:</c>.</summary>
    /// <remarks>
    /// Two doubles is a homogeneous float aggregate, which both supported ABIs pass
    /// in floating-point registers rather than on the stack - so the struct has to
    /// be declared as one, not flattened into two arguments.
    /// </remarks>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendSize(nint receiver, nint selector, CGSize arg1);

    /// <summary>For selectors taking a pointer and a length, such as <c>dataWithBytes:length:</c>.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendBytes(nint receiver, nint selector, nint bytes, nint length);

    /// <summary>The CoreGraphics <c>CGSize</c>, which is also AppKit's <c>NSSize</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct CGSize(double Width, double Height);

    /// <summary>For <c>stringWithUTF8String:</c>, whose argument is a UTF-8 C string.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SendUtf8(nint receiver, nint selector, string arg1);

    /// <summary>
    /// Wraps a managed string in an autoreleased <c>NSString</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>stringWithUTF8String:</c> rather than <c>alloc</c>/<c>initWithUTF8String:</c>
    /// so the result is autoreleased and nothing here has to own it. Every caller
    /// hands the result straight to a setter that retains what it is given.
    /// </para>
    /// <para>
    /// UTF-8 and not ANSI. The row's separator is U+00B7 and the menu is translated
    /// into five languages, so an ANSI round trip would replace exactly the
    /// characters this app was careful to get right everywhere else.
    /// </para>
    /// </remarks>
    internal static nint NSString(string value)
        => SendUtf8(Class("NSString"), sel_registerName("stringWithUTF8String:"), value);
}

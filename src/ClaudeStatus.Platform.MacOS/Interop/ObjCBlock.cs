using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeStatus.Platform.MacOS.Interop;

/// <summary>
/// The slice of the blocks ABI needed to hand a completion handler to a framework,
/// and to call one a framework handed us.
/// </summary>
/// <remarks>
/// <para>
/// <c>UNUserNotificationCenter</c> reports nearly everything through blocks: the
/// answer to a permission request, the current settings, whether a post worked.
/// There is no selector-and-target alternative, so the block has to be built by
/// hand. The layout is the documented one from the Clang blocks ABI and is the
/// same on arm64 and x86_64: <c>isa</c>, <c>flags</c>, <c>reserved</c>,
/// <c>invoke</c>, <c>descriptor</c>, then whatever the block captured.
/// </para>
/// <para>
/// <b>Marked global, allocated here.</b> A global block is never really copied:
/// <c>Block_copy</c> returns the same pointer and <c>Block_release</c> does nothing.
/// That is what lets a block be built without the runtime's copy and dispose
/// helpers - and it means this code owns the memory. It is freed by
/// <see cref="Free"/>, and only once the framework is done with it: the runtime
/// reads the flags again in <c>Block_release</c> after the invoke returns, so
/// freeing a block from inside its own callback is a use after free.
/// </para>
/// <para>
/// One captured slot, a pointer-sized context, which callers use for a
/// <see cref="GCHandle"/>. The callback itself is an <c>UnmanagedCallersOnly</c>
/// function, which cannot close over anything.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class ObjCBlock
{
    /// <summary><c>BLOCK_IS_GLOBAL</c>: never copied, never released.</summary>
    private const int BlockIsGlobal = 1 << 28;

    private const int InvokeOffset = 16;
    private const int ContextOffset = 32;
    private const int LiteralSize = 40;

    /// <summary>The shared descriptor: a reserved word and the literal's size.</summary>
    private static readonly nint Descriptor = CreateDescriptor();

    /// <summary>
    /// The class every global block points its <c>isa</c> at.
    /// </summary>
    /// <remarks>
    /// The address of the symbol is the value, as <c>&amp;_NSConcreteGlobalBlock</c>
    /// is in C. It lives in libSystem, which every process has loaded.
    /// </remarks>
    private static readonly nint GlobalBlockClass = NativeLibrary.GetExport(
        NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");

    /// <summary>
    /// Builds a block whose invoke is <paramref name="invoke"/> and whose captured
    /// context is <paramref name="context"/>.
    /// </summary>
    /// <param name="invoke">
    /// An <c>UnmanagedCallersOnly</c> function taking the block pointer first, then
    /// the block's own arguments.
    /// </param>
    internal static nint Create(nint invoke, nint context)
    {
        nint block = (nint)NativeMemory.AllocZeroed(LiteralSize);

        Marshal.WriteIntPtr(block, 0, GlobalBlockClass);
        Marshal.WriteInt32(block, 8, BlockIsGlobal);
        Marshal.WriteIntPtr(block, InvokeOffset, invoke);
        Marshal.WriteIntPtr(block, 24, Descriptor);
        Marshal.WriteIntPtr(block, ContextOffset, context);

        return block;
    }

    /// <summary>The context a block built by <see cref="Create"/> carries.</summary>
    internal static nint ContextOf(nint block) => Marshal.ReadIntPtr(block, ContextOffset);

    /// <summary>Frees a block built by <see cref="Create"/>.</summary>
    internal static void Free(nint block) => NativeMemory.Free((void*)block);

    /// <summary>Calls a block that takes nothing, such as a delegate's completion handler.</summary>
    internal static void Invoke(nint block)
    {
        var invoke = (delegate* unmanaged<nint, void>)Marshal.ReadIntPtr(block, InvokeOffset);
        invoke(block);
    }

    /// <summary>Calls a block that takes one pointer-sized value.</summary>
    /// <remarks>
    /// Covers an object argument and an <c>NSUInteger</c> alike: both travel in the
    /// first integer register after the block itself.
    /// </remarks>
    internal static void Invoke(nint block, nint argument)
    {
        var invoke = (delegate* unmanaged<nint, nint, void>)Marshal.ReadIntPtr(block, InvokeOffset);
        invoke(block, argument);
    }

    private static nint CreateDescriptor()
    {
        nint descriptor = (nint)NativeMemory.AllocZeroed(16);
        Marshal.WriteInt64(descriptor, 8, LiteralSize);
        return descriptor;
    }
}

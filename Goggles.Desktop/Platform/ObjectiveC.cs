using System.Runtime.InteropServices;

namespace Goggles.Desktop.Platform;

/// <summary>A Core Graphics rectangle, laid out as AppKit expects it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect(double x, double y, double width, double height)
{
    public double X = x;
    public double Y = y;
    public double Width = width;
    public double Height = height;
}

/// <summary>
/// The sliver of the Objective-C runtime Goggles needs to borrow macOS's own controls.
/// </summary>
/// <remarks>
/// Only simple pointer-in, pointer-out message sends are declared. Anything returning a struct
/// needs <c>objc_msgSend_stret</c> on some architectures and is deliberately avoided.
/// </remarks>
internal static partial class ObjectiveC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    private const int RtldNow = 2;

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GetClass(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr Selector(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr SendString(
        IntPtr receiver,
        IntPtr selector,
        string argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(
        IntPtr receiver,
        IntPtr selector,
        IntPtr first,
        IntPtr second,
        IntPtr third);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial void SendModifiers(IntPtr receiver, IntPtr selector, ulong modifiers);

    /// <summary>
    /// Passing a <c>CGRect</c> by value is fine; it is only struct <em>returns</em> that need
    /// <c>objc_msgSend_stret</c> on some architectures.
    /// </summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial void SendRect(IntPtr receiver, IntPtr selector, CGRect rect);

    /// <summary>For initialisers that take a rectangle and an object, such as
    /// <c>initWithFrame:configuration:</c>.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendRectAndObject(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr argument);

    /// <summary>Reads a rectangle back. Safe on arm64, where a four-double struct returns in registers.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    internal static partial CGRect SendGetRect(IntPtr receiver, IntPtr selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SendAction(
        IntPtr receiver,
        IntPtr selector,
        IntPtr action,
        IntPtr target,
        IntPtr sender);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DlOpen(string path, int mode);

    internal static void LoadFramework(string path) => DlOpen(path, RtldNow);

    /// <summary>Reads an <c>NSString</c> returned by a message send.</summary>
    internal static string? ReadString(IntPtr nsString) =>
        nsString == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUTF8(Send(nsString, Selector("UTF8String")));

    /// <summary>
    /// Sends a standard editing action to whatever currently has focus, which is what the Edit menu
    /// of a native application does.
    /// </summary>
    internal static void PerformOnFirstResponder(string selector)
    {
        var application = Send(GetClass("NSApplication"), Selector("sharedApplication"));

        SendAction(
            application,
            Selector("sendAction:to:from:"),
            Selector(selector),
            IntPtr.Zero,
            IntPtr.Zero);
    }
}

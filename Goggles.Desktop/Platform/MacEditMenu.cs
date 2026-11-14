using static Goggles.Desktop.Platform.ObjectiveC;

namespace Goggles.Desktop.Platform;

/// <summary>
/// Appends the standard macOS Edit menu to the application's menu bar.
/// </summary>
/// <remarks>
/// macOS dispatches ⌘C/⌘V through the main menu's key equivalents before the keystroke reaches the
/// focused view, so without an Edit menu you cannot paste a password into the embedded
/// <see cref="MacWebView"/>. Avalonia's own <c>NativeMenu</c> does not produce AppKit key
/// equivalents that reach a native child view, so the menu is built with AppKit directly and added
/// to the menu bar Avalonia has already installed — replacing the whole menu would take the
/// application menu, and Quit, with it.
/// <para>
/// Each item carries a nil target, which is what makes AppKit walk the responder chain and deliver
/// the action to whatever actually has focus.
/// </para>
/// </remarks>
internal static class MacEditMenu
{
    private const ulong Command = 1UL << 20;
    private const ulong Shift = 1UL << 17;

    public static void Install()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var application = Send(GetClass("NSApplication"), Selector("sharedApplication"));
        var mainMenu = Send(application, Selector("mainMenu"));

        if (mainMenu == IntPtr.Zero)
        {
            return;
        }

        var editMenu = Send(
            Send(GetClass("NSMenu"), Selector("alloc")),
            Selector("initWithTitle:"),
            NsString("Edit"));

        Add(editMenu, "Undo", "undo:", "z", Command);
        Add(editMenu, "Redo", "redo:", "z", Command | Shift);
        AddSeparator(editMenu);
        Add(editMenu, "Cut", "cut:", "x", Command);
        Add(editMenu, "Copy", "copy:", "c", Command);
        Add(editMenu, "Paste", "paste:", "v", Command);
        Add(editMenu, "Select All", "selectAll:", "a", Command);

        var editItem = Send(
            Send(GetClass("NSMenuItem"), Selector("alloc")),
            Selector("initWithTitle:action:keyEquivalent:"),
            NsString("Edit"),
            IntPtr.Zero,
            NsString(string.Empty));

        Send(editItem, Selector("setSubmenu:"), editMenu);
        Send(mainMenu, Selector("addItem:"), editItem);
    }

    private static void Add(IntPtr menu, string title, string selector, string key, ulong modifiers)
    {
        var item = Send(
            Send(GetClass("NSMenuItem"), Selector("alloc")),
            Selector("initWithTitle:action:keyEquivalent:"),
            NsString(title),
            Selector(selector),
            NsString(key));

        SendModifiers(item, Selector("setKeyEquivalentModifierMask:"), modifiers);
        Send(menu, Selector("addItem:"), item);
    }

    private static void AddSeparator(IntPtr menu) =>
        Send(menu, Selector("addItem:"), Send(GetClass("NSMenuItem"), Selector("separatorItem")));

    private static IntPtr NsString(string value) =>
        SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), value);
}

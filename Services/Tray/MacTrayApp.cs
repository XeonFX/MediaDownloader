using System.Runtime.InteropServices;
using System.Text;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Updates;

namespace MediaDownloader.Services.Tray;

/// <summary>
/// Hosts the app as a macOS menu-bar (status bar) agent. Creates an <c>NSStatusItem</c> whose
/// menu shows the current active downloads and total speed, a "Dashboard" item that opens the
/// running web UI in the browser, and "Quit".
///
/// This talks to AppKit directly through the Objective-C runtime (<c>libobjc</c>) so it needs no
/// extra NuGet dependencies. AppKit requires its run loop on the process's main thread, so
/// <see cref="Run"/> must be called from <c>Main</c> (it blocks until the user quits) while the
/// Kestrel web host runs on background threads.
/// </summary>
internal static unsafe class MacTrayApp
{
    // Objective-C activation policy: Accessory = menu-bar agent, no Dock icon.
    private const long NSApplicationActivationPolicyAccessory = 1;
    // NSCellImagePosition.NSImageLeft — icon to the left of the (speed) title.
    private const long NSImageLeft = 2;

    private static WebApplication _app = null!;
    private static DownloadManager _downloads = null!;
    private static UpdateService _updates = null!;
    private static string _dashboardUrl = "http://localhost:5170";

    private static IntPtr _statusButton;   // NSStatusBarButton
    private static IntPtr _menu;           // NSMenu
    private static IntPtr _delegate;       // our MDTrayDelegate instance (menu delegate + action target)

    /// <summary>
    /// Builds the status-bar item and runs the AppKit event loop. Blocks until the user picks Quit,
    /// at which point the process exits. Must be invoked on the main thread.
    /// </summary>
    public static void Run(WebApplication app, string dashboardUrl, DownloadManager downloads,
        UpdateService updates)
    {
        _app = app;
        _downloads = downloads;
        _updates = updates;
        _dashboardUrl = dashboardUrl;

        // AppKit (and its Foundation dependency) aren't loaded into a plain .NET process; load it so
        // objc_getClass can resolve NSApplication/NSStatusBar/etc.
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

        var app_ = Msg(Cls("NSApplication"), Sel("sharedApplication"));
        MsgL(app_, Sel("setActivationPolicy:"), NSApplicationActivationPolicyAccessory);

        RegisterDelegateClass();
        _delegate = Msg(Cls("MDTrayDelegate"), Sel("new"));

        BuildStatusItem();
        UpdateButtonTitle();

        // Refresh the menu-bar speed readout on the main run loop every 2s.
        MsgTimer(Cls("NSTimer"), Sel("scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:"),
            2.0, _delegate, Sel("tick:"), IntPtr.Zero, true);

        // Blocks, driving the AppKit run loop, until Quit calls Environment.Exit.
        Msg(app_, Sel("run"));
    }

    private static void BuildStatusItem()
    {
        var statusBar = Msg(Cls("NSStatusBar"), Sel("systemStatusBar"));
        // NSVariableStatusItemLength == -1.
        var statusItem = MsgD(statusBar, Sel("statusItemWithLength:"), -1.0);
        _statusButton = Msg(statusItem, Sel("button"));

        // Branded glyph shipped in Assets (template image auto-adapts to light/dark menu bar);
        // falls back to an SF Symbol if the asset isn't next to the executable.
        var img = LoadMenuBarImage();
        if (img != IntPtr.Zero)
        {
            Msg1(_statusButton, Sel("setImage:"), img);
            MsgL(_statusButton, Sel("setImagePosition:"), NSImageLeft);
        }

        _menu = Msg(Msg(Cls("NSMenu"), Sel("alloc")), Sel("init"));
        Msg1(_menu, Sel("setDelegate:"), _delegate);   // menuNeedsUpdate: rebuilds it on open
        Msg1(statusItem, Sel("setMenu:"), _menu);
    }

    /// <summary>
    /// Loads Assets/MenuBarIcon.png (with its @2x sibling picked up automatically) as a template
    /// image so it recolours for light/dark menu bars. Falls back to an SF Symbol.
    /// </summary>
    private static IntPtr LoadMenuBarImage()
    {
        var path = Path.Combine(AppPaths.ContentDirectory, "Assets", "MenuBarIcon.png");
        if (File.Exists(path))
        {
            var img = Msg1(Msg(Cls("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), NSString(path));
            if (img != IntPtr.Zero)
            {
                MsgB(img, Sel("setTemplate:"), true);
                MsgSize(img, Sel("setSize:"), 18, 18);   // points; the @2x file covers Retina
                return img;
            }
        }
        return Msg2(Cls("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"),
            NSString("arrow.down.circle"), NSString("MediaDownloader"));
    }

    /// <summary>Rebuilds the dropdown from live download state. Runs on the main thread.</summary>
    private static void RebuildMenu()
    {
        Msg(_menu, Sel("removeAllItems"));

        foreach (var entry in TrayMenuModel.Build(_downloads, _updates, _dashboardUrl))
        {
            switch (entry)
            {
                case TrayLabel label: AddDisabled(label.Text); break;
                case TraySeparator: AddSeparator(); break;
                case TrayCommand command: AddAction(command.Text, ActionSelector(command.Action)); break;
            }
        }
    }

    private static string ActionSelector(TrayAction action) => action switch
    {
        TrayAction.Dashboard => "openDashboard:",
        TrayAction.CheckUpdates => "checkUpdates:",
        TrayAction.InstallUpdate => "installUpdate:",
        TrayAction.Quit => "quit:",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static void UpdateButtonTitle()
    {
        if (_statusButton == IntPtr.Zero) return;
        var speed = TrayMenuModel.SpeedText(_downloads);
        var title = speed.Length > 0 ? " " + speed : "";
        Msg1(_statusButton, Sel("setTitle:"), NSString(title));
    }

    // ---- menu helpers ----

    private static void AddDisabled(string title)
    {
        // No action selector -> menu auto-disables (renders greyed, acts as a header/status row).
        Msg3(_menu, Sel("addItemWithTitle:action:keyEquivalent:"), NSString(title), IntPtr.Zero, NSString(""));
    }

    private static void AddAction(string title, string selector)
    {
        var item = Msg3(_menu, Sel("addItemWithTitle:action:keyEquivalent:"),
            NSString(title), Sel(selector), NSString(""));
        Msg1(item, Sel("setTarget:"), _delegate);
    }

    private static void AddSeparator()
    {
        Msg1(_menu, Sel("addItem:"), Msg(Cls("NSMenuItem"), Sel("separatorItem")));
    }

    // ---- Objective-C callbacks (must be static, IntPtr-only signatures) ----

    [UnmanagedCallersOnly]
    private static void MenuNeedsUpdate(IntPtr self, IntPtr cmd, IntPtr menu)
    {
        try { RebuildMenu(); } catch { /* never throw across the native boundary */ }
    }

    [UnmanagedCallersOnly]
    private static void Tick(IntPtr self, IntPtr cmd, IntPtr timer)
    {
        try { UpdateButtonTitle(); } catch { }
    }

    [UnmanagedCallersOnly]
    private static void OpenDashboard(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { TrayActions.OpenUrl(_dashboardUrl); } catch { }
    }

    [UnmanagedCallersOnly]
    private static void CheckUpdates(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { TrayActions.CheckUpdates(_updates); } catch { }
    }

    [UnmanagedCallersOnly]
    private static void InstallUpdate(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { TrayActions.InstallUpdate(_updates); } catch { }
    }

    [UnmanagedCallersOnly]
    private static void Quit(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        TrayActions.Quit(_app);
    }

    private static void RegisterDelegateClass()
    {
        // A single NSObject subclass acts as both the menu delegate and the menu-item action target.
        var cls = objc_allocateClassPair(Cls("NSObject"), "MDTrayDelegate", IntPtr.Zero);

        class_addMethod(cls, Sel("menuNeedsUpdate:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MenuNeedsUpdate, "v@:@");
        class_addMethod(cls, Sel("tick:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Tick, "v@:@");
        class_addMethod(cls, Sel("openDashboard:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OpenDashboard, "v@:@");
        class_addMethod(cls, Sel("checkUpdates:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&CheckUpdates, "v@:@");
        class_addMethod(cls, Sel("installUpdate:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&InstallUpdate, "v@:@");
        class_addMethod(cls, Sel("quit:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Quit, "v@:@");

        objc_registerClassPair(cls);
    }

    // ---- Objective-C runtime interop ----

    private const string Libobjc = "/usr/lib/libobjc.dylib";

    [DllImport(Libobjc, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Libobjc, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Libobjc)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr extraBytes);

    [DllImport(Libobjc)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Libobjc)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg1(IntPtr receiver, IntPtr sel, IntPtr a);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg2(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg3(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgL(IntPtr receiver, IntPtr sel, long a);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgD(IntPtr receiver, IntPtr sel, double a);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgB(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.U1)] bool a);

    // NSSize is two doubles passed by value — identical ABI to two double args on arm64/x64.
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSize(IntPtr receiver, IntPtr sel, double w, double h);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgTimer(IntPtr receiver, IntPtr sel, double interval,
        IntPtr target, IntPtr selector, IntPtr userInfo, [MarshalAs(UnmanagedType.U1)] bool repeats);

    private static IntPtr Cls(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>Creates an autoreleased NSString from a managed string.</summary>
    private static IntPtr NSString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        fixed (byte* p = bytes)
        {
            // stringWithUTF8String: needs a NUL-terminated buffer; GetBytes has no terminator, so use
            // the length-aware initializer instead.
            var alloc = Msg(Cls("NSString"), Sel("alloc"));
            return MsgStr(alloc, Sel("initWithBytes:length:encoding:"), (IntPtr)p, (nint)bytes.Length, 4 /* NSUTF8StringEncoding */);
        }
    }

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgStr(IntPtr receiver, IntPtr sel, IntPtr bytes, nint length, nint encoding);
}

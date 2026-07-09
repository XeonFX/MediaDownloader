using System.Runtime.InteropServices;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Updates;

namespace MediaDownloader.Services.Tray;

/// <summary>
/// Hosts the app as a Windows system-tray (notification area) icon. Creates a hidden
/// message-only window that owns a <c>Shell_NotifyIcon</c> entry; right/left click pops up a
/// context menu built from <see cref="TrayMenuModel"/> — the same menu content
/// <see cref="MacTrayApp"/> renders into an <c>NSMenu</c>, so the two stay in sync automatically.
///
/// This talks to user32/shell32 directly through P/Invoke (no extra NuGet deps or WinForms/WPF
/// dependency, mirroring <see cref="MacTrayApp"/>'s use of the Objective-C runtime), which also
/// avoids retargeting the project to a Windows-specific TFM just for a tray icon.
/// </summary>
internal static unsafe class WindowsTrayApp
{
    private const string ClassName = "MediaDownloaderTrayWindow";
    private const uint WM_TRAYICON = 0x8000 + 1; // WM_APP + 1
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private static WebApplication _app = null!;
    private static DownloadManager _downloads = null!;
    private static UpdateService _updates = null!;
    private static string _dashboardUrl = "http://localhost:47820";

    private static IntPtr _hWnd;
    private static NOTIFYICONDATA _iconData;

    /// <summary>
    /// Registers the window class/tray icon and runs the Win32 message loop. Blocks until Quit
    /// calls <see cref="Environment.Exit(int)"/>. Kestrel must already be running on background
    /// threads (via <c>WebApplication.StartAsync</c>) before calling this.
    /// </summary>
    public static void Run(WebApplication app, string dashboardUrl, DownloadManager downloads, UpdateService updates)
    {
        _app = app;
        _downloads = downloads;
        _updates = updates;
        _dashboardUrl = dashboardUrl;

        var hInstance = GetModuleHandleW(null);
        RegisterWindowClass(hInstance);

        // HWND_MESSAGE parent -> a message-only window: no taskbar entry, no visible window,
        // just something that can own a notification-area icon and receive its callbacks.
        _hWnd = CreateWindowExW(0, ClassName, "MediaDownloader", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);

        _iconData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (int)WM_TRAYICON,
            hIcon = LoadTrayIcon(),
            szTip = "MediaDownloader",
        };
        Shell_NotifyIconW(NIM_ADD, ref _iconData);

        SetTimer(_hWnd, (UIntPtr)1, 2000, IntPtr.Zero);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        Shell_NotifyIconW(NIM_DELETE, ref _iconData);
    }

    private static IntPtr LoadTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
        if (File.Exists(path))
        {
            var icon = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (icon != IntPtr.Zero) return icon;
        }
        return LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION fallback
    }

    private static void UpdateTooltip()
    {
        var speed = TrayMenuModel.SpeedText(_downloads);
        var tip = speed.Length > 0 ? $"MediaDownloader — ↓ {speed}" : "MediaDownloader";
        _iconData.szTip = tip.Length > 127 ? tip[..127] : tip;
        Shell_NotifyIconW(NIM_MODIFY, ref _iconData);
    }

    /// <summary>Builds and shows the popup menu at the cursor, then dispatches whichever item was picked.</summary>
    private static void ShowContextMenu(IntPtr hWnd)
    {
        var entries = TrayMenuModel.Build(_downloads, _updates, _dashboardUrl);
        var hMenu = CreatePopupMenu();
        var idToAction = new Dictionary<int, TrayAction>();
        var nextId = 1;

        try
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case TrayLabel label:
                        AppendMenuW(hMenu, MF_STRING | MF_GRAYED, UIntPtr.Zero, label.Text);
                        break;
                    case TraySeparator:
                        AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                        break;
                    case TrayCommand command:
                        var id = nextId++;
                        idToAction[id] = command.Action;
                        AppendMenuW(hMenu, MF_STRING, (UIntPtr)id, command.Text);
                        break;
                }
            }

            GetCursorPos(out var pt);
            // Required so the menu dismisses correctly when the user clicks away from it.
            SetForegroundWindow(hWnd);
            var cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, hWnd, IntPtr.Zero);
            PostMessageW(hWnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            if (cmd != 0 && idToAction.TryGetValue(cmd, out var action))
                Dispatch(action);
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private static void Dispatch(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.Dashboard: TrayActions.OpenUrl(_dashboardUrl); break;
            case TrayAction.CheckUpdates: TrayActions.CheckUpdates(_updates); break;
            case TrayAction.InstallUpdate: TrayActions.InstallUpdate(_updates); break;
            case TrayAction.Quit:
                Shell_NotifyIconW(NIM_DELETE, ref _iconData);
                TrayActions.Quit(_app);
                break;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAYICON)
            {
                var mouseMsg = (uint)lParam.ToInt64();
                if (mouseMsg is WM_LBUTTONUP or WM_RBUTTONUP) ShowContextMenu(hWnd);
                return IntPtr.Zero;
            }
            if (msg == WM_TIMER)
            {
                UpdateTooltip();
                return IntPtr.Zero;
            }
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        catch { /* never throw across the native boundary */ }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void RegisterWindowClass(IntPtr hInstance)
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);
    }

    // ---- Win32 interop ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);
}

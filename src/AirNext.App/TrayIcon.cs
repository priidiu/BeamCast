using System.Runtime.InteropServices;

namespace AirNext_App;

/// <summary>
/// Tray icon (NotifyIcon) przez Win32 interop — RHI-168.
/// WinUI 3 has no native NotifyIcon; we call Shell_NotifyIconW from shell32.dll.
///
/// KEY: use the REAL WinUI HWND (WindowNative.GetWindowHandle).
/// Do not RegisterClassEx a custom window (flaky under WinUI 3).
/// Tray clicks: subclass the WinUI window → DispatcherQueue onto the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAY_CALLBACK = WM_USER + 1;
    private const int IDI_APPLICATION = 32512;
    private const int GWLP_WNDPROC = -4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint WM_NULL = 0x0000;
    private const uint IdShow = 1;
    private const uint IdExit = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly string _title;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcher;
    private readonly IntPtr _hwnd;
    private IntPtr _hIcon;
    private IntPtr _prevWndProc;
    private WndProcDelegate? _wndProcDelegate; // keep alive (GC!)
    private bool _added;
    private bool _disposed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Right-click the icon — open the menu.</summary>
    public event Action? OnTrayMenuRequested;

    /// <summary>Double-click — open the main window.</summary>
    public event Action? OnTrayDoubleClick;

    /// <summary>Tray menu Exit — quit the process.</summary>
    public event Action? OnExitRequested;

    public TrayIcon(string title, Microsoft.UI.Dispatching.DispatcherQueue uiDispatcher, IntPtr hwnd)
    {
        _title = title;
        _uiDispatcher = uiDispatcher;
        _hwnd = hwnd;
    }

    public void Start()
    {
        _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));

        // Subclass the WinUI window — catch WM_TRAY_CALLBACK (icon click)
        _wndProcDelegate = TrayWndProc;
        _prevWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        AppLog.Write("Tray: subclass prev=" + _prevWndProc);

        var nid = BuildNotifyIconData(_hwnd, _hIcon, _title);
        _added = Shell_NotifyIcon(NIM_ADD, ref nid);
        AppLog.Write("Tray: Shell_NotifyIcon NIM_ADD = " + _added);
    }

    private IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY_CALLBACK)
        {
            uint eventType = (uint)lParam.ToInt64();
            if (eventType == WM_RBUTTONUP)
            {
                _ = _uiDispatcher.TryEnqueue(HandleTrayMenu);
            }
            else if (eventType == WM_LBUTTONDBLCLK)
            {
                _ = _uiDispatcher.TryEnqueue(() => OnTrayDoubleClick?.Invoke());
            }
        }
        // Forward to the original WinUI WndProc
        return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    private void HandleTrayMenu()
    {
        OnTrayMenuRequested?.Invoke();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        try
        {
            AppendMenuW(menu, MF_STRING, new UIntPtr(IdShow), "Open BeamCast");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            AppendMenuW(menu, MF_STRING, new UIntPtr(IdExit), "Exit");
            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd == IdShow)
                OnTrayDoubleClick?.Invoke();
            else if (cmd == IdExit)
                OnExitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static NOTIFYICONDATA BuildNotifyIconData(IntPtr hwnd, IntPtr hicon, string tip)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = hicon,
            szTip = tip,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var nid = BuildNotifyIconData(_hwnd, _hIcon, _title);
            Shell_NotifyIcon(NIM_DELETE, ref nid);
        }
        // Restore original WndProc
        if (_prevWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _prevWndProc);
        }
    }
}

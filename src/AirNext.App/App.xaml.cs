using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AirNext_App;

/// <summary>
/// Aplikacja BeamCast (AirNext UI, RHI-168).
/// WinUI 3 + Fluent Design Windows 11 — theme follows the OS.
/// Tray (NotifyIcon) przez Win32 interop — WinUI 3 nie ma natywnego NotifyIcon.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private TrayIcon? _tray;

    /// <summary>Inicjalizuje singleton aplikacji — pierwszy kod uruchamiany (odpowiednik main/WinMain).</summary>
    public App()
    {
        AppLog.Write("App ctor: start");
        InitializeComponent();
        AppLog.Write("App ctor: InitializeComponent OK");
    }

    /// <summary>Show the main window again (from tray).</summary>
    private void ActivateWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            AppLog.Write("ActivateWindow: created MainWindow");
        }
        if (_window is MainWindow mw)
            mw.ShowFromTray();
        else
            _window.Activate();
    }

    private async Task ExitFromTrayAsync()
    {
        AppLog.Write("App: Exit from tray");
        try
        {
            if (_window is MainWindow mw)
                await mw.ExitAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"App: ExitFromTray FAIL: {ex}");
        }
        _tray?.Dispose();
        Exit();
    }

    /// <summary>Called when the app is launched.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Write("OnLaunched: start");
        try
        {
            _window = new MainWindow();
            AppLog.Write("OnLaunched: MainWindow created");
            _window.Activate();
            AppLog.Write("OnLaunched: activated");
            // Compact window (tray tool) — AFTER Activate (AppWindow.Resize before
            // Activate bywa ignorowane)
            if (_window is MainWindow mw)
            {
                mw.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 560));
                AppLog.Write("OnLaunched: resized to 460x560");
            }

            // Tray start PO Activate — DispatcherQueue + HWND okna WinUI pewne
            try
            {
                var uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                // Real WinUI HWND (WinRT interop) — tray needs a valid hWnd
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                _tray = new TrayIcon("BeamCast", uiDispatcher, hwnd);
                _tray.OnTrayDoubleClick += () => ActivateWindow();
                _tray.OnExitRequested += () => _ = ExitFromTrayAsync();
                _tray.Start();
                AppLog.Write("OnLaunched: tray started (hwnd=" + hwnd + ")");
            }
            catch (Exception trayEx)
            {
                AppLog.Write($"OnLaunched: tray FAIL: {trayEx}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"OnLaunched: FAIL: {ex}");
        }
    }
}

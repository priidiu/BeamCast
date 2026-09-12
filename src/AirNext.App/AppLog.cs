using System.IO;

namespace AirNext_App;

/// <summary>
/// Minimalny logger do pliku (diagnostyka startu WinUI 3 — RHI-168).
/// WinUI has no console; log to %LOCALAPPDATA%\BeamCast\beancast.log.
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();
    private static string? _path;

    private static string Path
    {
        get
        {
            if (_path is null)
            {
                var dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                _path = System.IO.Path.Combine(dir, "BeamCast", "beancast.log");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            }
            return _path;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}

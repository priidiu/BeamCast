using System.Text.Json;
using Microsoft.Win32;

namespace AirNext_App;

/// <summary>
/// M4 settings persistence (RHI-171) — %LOCALAPPDATA%\BeamCast\settings.json.
/// </summary>
public sealed class AppSettings
{
    public bool AutoStreamLastDevice { get; set; } = true;
    /// <summary>
    /// Mute master volume of the default render device. WASAPI loopback captures
    /// that mix — enabling this typically yields silence on AirPlay unless a
    /// virtual cable is used. Default off.
    /// </summary>
    public bool AutoMutePc { get; set; }
    public bool StartWithWindows { get; set; }
    public bool RealTimeMode { get; set; }
    /// <summary>RealTime delay in ms (WASAPI + sync 84). 20–250, default 40.</summary>
    public int RealTimeDelayMs { get; set; } = 40;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamCast", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s is not null)
                {
                    AppLog.Write($"Settings: loaded autoStream={s.AutoStreamLastDevice} mute={s.AutoMutePc} run={s.StartWithWindows} rt={s.RealTimeMode} delay={s.RealTimeDelayMs}");
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Settings: load FAIL: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            ApplyStartWithWindows();
            AppLog.Write($"Settings: saved autoStream={AutoStreamLastDevice} mute={AutoMutePc} run={StartWithWindows} rt={RealTimeMode} delay={RealTimeDelayMs}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Settings: save FAIL: {ex.Message}");
        }
    }

    private void ApplyStartWithWindows()
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;
            const string name = "BeamCast";
            var exe = Environment.ProcessPath;
            if (StartWithWindows && !string.IsNullOrEmpty(exe))
                key.SetValue(name, "\"" + exe + "\"");
            else
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Settings: StartWithWindows registry FAIL: {ex.Message}");
        }
    }
}

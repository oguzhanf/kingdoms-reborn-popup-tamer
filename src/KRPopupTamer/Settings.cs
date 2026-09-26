using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace KRPopupTamer;

// User choices and lifetime statistics, stored in %APPDATA%\KRPopupTamer\settings.json.
sealed class Settings
{
    public Dictionary<string, bool> Enabled { get; set; } = [];
    public Dictionary<string, long> TotalSuppressed { get; set; } = [];
    public bool StartMinimized { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KRPopupTamer", "settings.json");

    public bool IsEnabled(GameOption option) => Enabled.TryGetValue(option.Id, out var on) ? on : option.DefaultOn;

    public static Settings Load()
    {
        Settings? settings = null;
        try
        {
            if (File.Exists(FilePath)) settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
        }
        settings ??= new();
        settings.Enabled ??= [];
        settings.TotalSuppressed ??= [];
        return settings;
    }

    // Returns an error message instead of throwing; the next save retries.
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not save settings: {e.Message}";
        }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "KRPopupTamer";
    static string RunCommand => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) as string == RunCommand;
        }
    }

    // Returns an error message instead of throwing.
    public static string? SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(RunValue, RunCommand);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
        {
            return $"Could not change the Windows startup entry: {e.Message}";
        }
    }
}

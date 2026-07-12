using System;
using System.IO;
using System.Text.Json;

namespace CertConvert.Services;

/// <summary>
/// The app's only persisted state: a single JSON file holding preferences.
/// Anything here must be documented in the README's "Where things live".
/// </summary>
public sealed class AppSettings
{
    public bool CheckForUpdatesOnLaunch { get; set; }

    private string _directory = DefaultDirectory;

    /// <summary>
    /// macOS: ~/Library/Application Support/CertConvert. Windows: %APPDATA%\CertConvert.
    /// Built explicitly on macOS — .NET maps ApplicationData to ~/.config on Unix,
    /// which is not the native location.
    /// </summary>
    public static string DefaultDirectory =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "CertConvert")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CertConvert");

    public static string SettingsPath(string? directory = null) =>
        Path.Combine(directory ?? DefaultDirectory, "settings.json");

    /// <summary>Missing or unreadable settings mean defaults — never an error.</summary>
    public static AppSettings Load(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath(dir)));
            if (settings is not null)
            {
                settings._directory = dir;
                return settings;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings { _directory = dir };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(SettingsPath(_directory),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference that fails to persist is not worth interrupting the user for.
        }
    }
}

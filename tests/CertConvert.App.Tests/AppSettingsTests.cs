using System.IO;
using CertConvert.Services;

namespace CertConvert.Gui.Tests;

public class AppSettingsTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "certconvert-settings-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var settings = AppSettings.Load(TempDir());
        Assert.False(settings.CheckForUpdatesOnLaunch);
    }

    [Fact]
    public void Toggle_RoundTrips()
    {
        var dir = TempDir();
        var settings = AppSettings.Load(dir);
        settings.CheckForUpdatesOnLaunch = true;
        settings.Save();

        var reloaded = AppSettings.Load(dir);
        Assert.True(reloaded.CheckForUpdatesOnLaunch);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        var dir = TempDir();
        File.WriteAllText(AppSettings.SettingsPath(dir), "{ this is not valid json");
        var settings = AppSettings.Load(dir);
        Assert.False(settings.CheckForUpdatesOnLaunch);
    }
}

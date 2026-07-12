using Avalonia;
using Avalonia.Headless;
using CertConvert.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace CertConvert.Gui.Tests;

/// <summary>
/// Boots the real App on Avalonia's headless platform for UI tests.
/// Skia (rather than the null drawing backend) is used so frames can be
/// captured for README screenshots.
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Tests must never hit the network, whatever the user's settings say.
        System.Environment.SetEnvironmentVariable("CERTCONVERT_DISABLE_UPDATE_CHECK", "1");
        return AppBuilder.Configure<global::CertConvert.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
    }
}

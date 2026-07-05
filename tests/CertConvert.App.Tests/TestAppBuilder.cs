using Avalonia;
using Avalonia.Headless;
using CertConvert.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace CertConvert.Gui.Tests;

/// <summary>Boots the real App on Avalonia's headless platform for UI tests.</summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::CertConvert.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

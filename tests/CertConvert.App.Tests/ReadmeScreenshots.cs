using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using CertConvert.Core;
using CertConvert.ViewModels;
using CertConvert.Views;

namespace CertConvert.Gui.Tests;

/// <summary>
/// Renders README screenshots from the real app. No-op unless
/// CERTCONVERT_CAPTURE_DIR is set, so normal test runs are unaffected:
///   CERTCONVERT_CAPTURE_DIR=/tmp/shots dotnet test --filter ReadmeScreenshots
/// </summary>
public class ReadmeScreenshots
{
    [AvaloniaFact]
    public void CaptureInspectPage()
    {
        var dir = Environment.GetEnvironmentVariable("CERTCONVERT_CAPTURE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        using var key = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        using var cert = Generator.CreateSelfSigned(key.Key, new CertSpec
        {
            CommonName = "device.example.com",
            Organization = "Example Ltd",
            Country = "GB",
            DnsNames = ["device.example.com", "iot.example.com"],
            IpAddresses = ["192.0.2.10"],
            ValidityDays = 365,
        });
        var pem = Path.Combine(Path.GetTempPath(), "device.example.com.pem");
        File.WriteAllText(pem, cert.ExportCertificatePem());

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Inspect.LoadPath(pem);

            foreach (var (variant, name) in new (ThemeVariant, string)[]
                     { (ThemeVariant.Light, "inspect-light"), (ThemeVariant.Dark, "inspect-dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame!.Save(Path.Combine(dir, $"{name}.png"));
            }
        }
        finally
        {
            File.Delete(pem);
        }
    }
}

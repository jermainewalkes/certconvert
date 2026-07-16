using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CertConvert.Core;
using CertConvert.ViewModels;
using CertConvert.Views;

namespace CertConvert.Gui.Tests;

// App Store screenshots at 2560x1600 (a valid macOS App Store size, 16:10).
public class StoreShots
{
    [AvaloniaFact]
    public void Capture()
    {
        var dir = Environment.GetEnvironmentVariable("CERTCONVERT_CAPTURE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        // 1 — Inspect a real cert.
        using var key = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        using var cert = Generator.CreateSelfSigned(key.Key, new CertSpec
        {
            CommonName = "device.example.com", Organization = "Example Ltd", Country = "GB",
            DnsNames = ["device.example.com", "iot.example.com"], IpAddresses = ["192.0.2.10"],
            ValidityDays = 365,
        });
        var pem = Path.Combine(Path.GetTempPath(), "device.example.com.pem");
        File.WriteAllText(pem, cert.ExportCertificatePem());
        vm.Inspect.LoadPath(pem);
        Shot(window, dir, "01-inspect");

        vm.SelectedIndex = 1; Shot(window, dir, "02-convert");
        vm.SelectedIndex = 2; Shot(window, dir, "03-chain");
        vm.SelectedIndex = 3; Shot(window, dir, "04-keys");
        vm.Generate.CommonName = "device.local"; vm.Generate.Organization = "Example Ltd";
        vm.Generate.Country = "GB"; vm.Generate.DnsNames = "device.local";
        vm.SelectedIndex = 4; Shot(window, dir, "05-generate");

        File.Delete(pem);
    }

    private static void Shot(MainWindow w, string dir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
    }
}

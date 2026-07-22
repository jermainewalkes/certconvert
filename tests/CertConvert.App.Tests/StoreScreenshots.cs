using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

        // CERTCONVERT_CAPTURE_SIZE sets the LOGICAL window size and
        // CERTCONVERT_CAPTURE_SCALE the render scaling; the PNG comes out at
        // size × scale. Real machines run scaled displays, so screenshots
        // should too — e.g. 1280x720 at 1.5 = a typical 150% 1080p laptop
        // (Microsoft Store), 1280x800 at 2 = Retina 2560x1600 (Mac App Store).
        int width = 1280, height = 800;
        var size = Environment.GetEnvironmentVariable("CERTCONVERT_CAPTURE_SIZE");
        if (!string.IsNullOrEmpty(size) && size.Split('x') is [var ws, var hs]
            && int.TryParse(ws, out int pw) && int.TryParse(hs, out int ph))
            (width, height) = (pw, ph);
        double scale = 1;
        var scaleVar = Environment.GetEnvironmentVariable("CERTCONVERT_CAPTURE_SCALE");
        if (!string.IsNullOrEmpty(scaleVar) &&
            double.TryParse(scaleVar, System.Globalization.CultureInfo.InvariantCulture, out double ps))
            scale = ps;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        if (scale != 1)
            window.SetRenderScaling(scale);

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

        // 6 — Chain with a real root→issuing→server chain, dropped shuffled and
        // validated (system roots off, so the dropped root is the trust anchor).
        var chainFiles = MakeChainPems();
        vm.SelectedIndex = 2;
        vm.Chain.AddDroppedFiles(chainFiles);
        vm.Chain.ValidateChainCommand.Execute(null);
        Shot(window, dir, "06-chain-validated");

        // 7 — Keys with a freshly generated key loaded.
        using var keysKey = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        var keyPem = Path.Combine(Path.GetTempPath(), "server.key");
        File.WriteAllText(keyPem, KeyTools.ExportPem(keysKey.Key, KeyOutputFormat.Pkcs8Pem));
        vm.SelectedIndex = 3;
        vm.Keys.LoadDroppedKey(keyPem);
        Shot(window, dir, "07-keys-loaded");

        File.Delete(pem);
        File.Delete(keyPem);
        foreach (var f in chainFiles) File.Delete(f);
    }

    /// <summary>Root CA → issuing CA → server certificate, written as three PEMs
    /// (returned deliberately out of order — the app sorts them).</summary>
    private static string[] MakeChainPems()
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var rootRsa = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Example Root CA, O=Example Ltd, C=GB",
            rootRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(notBefore, notBefore.AddYears(10));

        using var intRsa = RSA.Create(2048);
        var intReq = new CertificateRequest("CN=Example Issuing CA, O=Example Ltd, C=GB",
            intRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        using var inter = intReq.Create(root, notBefore, notBefore.AddYears(5), [1, 2, 3, 4]);
        using var interWithKey = inter.CopyWithPrivateKey(intRsa);

        using var leafRsa = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=www.example.com, O=Example Ltd, C=GB",
            leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("www.example.com");
        leafReq.CertificateExtensions.Add(san.Build());
        using var leaf = leafReq.Create(interWithKey, notBefore, notBefore.AddYears(1), [5, 6, 7, 8]);

        // Relative paths on purpose: the Chain page lists exactly what was
        // dropped, and bare filenames read better in the screenshot.
        var entries = new (string Name, X509Certificate2 Cert)[]
        {
            ("www.example.com.pem", leaf), ("example-root-ca.pem", root), ("example-issuing-ca.pem", inter),
        };
        var files = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            files[i] = entries[i].Name;
            File.WriteAllText(files[i], entries[i].Cert.ExportCertificatePem());
        }
        return files;
    }

    private static void Shot(MainWindow w, string dir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
    }
}

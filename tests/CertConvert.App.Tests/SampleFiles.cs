using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Avalonia.Headless.XUnit;
using CertConvert.Core;

namespace CertConvert.Gui.Tests;

// Generates the App Review sample file set (guideline 2.1(a)) into
// CERTCONVERT_SAMPLES_DIR, using CertConvert's own Core library so the files
// are exactly what the app produces and consumes. All names/data are fictional.
// Run: CERTCONVERT_SAMPLES_DIR=site/samples dotnet test --filter SampleFiles
public class SampleFiles
{
    private const string Pw = "certconvert";  // documented in the samples README

    [AvaloniaFact]
    public void Generate()
    {
        var dir = Environment.GetEnvironmentVariable("CERTCONVERT_SAMPLES_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        // --- a device key + self-signed certificate, in every format ---------
        using var key = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        File.WriteAllText(Path.Combine(dir, "device-key.pem"),
            KeyTools.ExportPem(key.Key, KeyOutputFormat.Pkcs8Pem));
        File.WriteAllText(Path.Combine(dir, "device-key-encrypted.pem"),
            KeyTools.ExportPem(key.Key, KeyOutputFormat.Pkcs8EncryptedPem, Pw));

        using var cert = Generator.CreateSelfSigned(key.Key, new CertSpec
        {
            CommonName = "device.example.com", Organization = "Example Ltd",
            OrganizationalUnit = "IoT", Country = "GB",
            DnsNames = ["device.example.com", "iot.example.com"],
            IpAddresses = ["192.0.2.10"], ValidityDays = 825,
        });
        X509Certificate2[] one = [cert];
        File.WriteAllBytes(Path.Combine(dir, "device-cert.pem"), Converter.Export(one, CertOutputFormat.Pem));
        File.WriteAllBytes(Path.Combine(dir, "device-cert.cer"), Converter.Export(one, CertOutputFormat.Der));
        File.WriteAllBytes(Path.Combine(dir, "device-cert.p7b"), Converter.Export(one, CertOutputFormat.Pkcs7Der));
        File.WriteAllBytes(Path.Combine(dir, "device.pfx"),
            Converter.Export(one, CertOutputFormat.Pkcs12, new ExportOptions { Password = Pw, PrivateKey = key.Key }));

        // --- a certificate signing request -----------------------------------
        File.WriteAllText(Path.Combine(dir, "request.csr"),
            Generator.CreateCsrPem(key.Key, new CertSpec
            {
                CommonName = "www.example.com", Organization = "Example Ltd", Country = "GB",
                DnsNames = ["www.example.com"],
            }));

        // --- a real root -> issuing CA -> server chain -----------------------
        var (rootPem, intPem, leafPem) = BuildChain();
        File.WriteAllText(Path.Combine(dir, "root-ca.pem"), rootPem);
        File.WriteAllText(Path.Combine(dir, "intermediate-ca.pem"), intPem);
        File.WriteAllText(Path.Combine(dir, "server.pem"), leafPem);
        // A single bundle in mixed order, to demonstrate auto-ordering + validation.
        File.WriteAllText(Path.Combine(dir, "chain-bundle.pem"), leafPem + rootPem + intPem);

        File.WriteAllText(Path.Combine(dir, "README.txt"), Readme());
    }

    private static (string root, string inter, string leaf) BuildChain()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Example Root CA, O=Example Ltd, C=GB",
            rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(from, from.AddYears(10));

        using var intKey = RSA.Create(2048);
        var intReq = new CertificateRequest("CN=Example Issuing CA, O=Example Ltd, C=GB",
            intKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        using var inter = intReq.Create(root, from, from.AddYears(5), [1, 2, 3, 4]);
        using var interWithKey = inter.CopyWithPrivateKey(intKey);

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=www.example.com, O=Example Ltd, C=GB",
            leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("www.example.com");
        leafReq.CertificateExtensions.Add(san.Build());
        using var leaf = leafReq.Create(interWithKey, from, from.AddYears(1), [5, 6, 7, 8]);

        return (root.ExportCertificatePem() + "\n",
                inter.ExportCertificatePem() + "\n",
                leaf.ExportCertificatePem() + "\n");
    }

    private static string Readme() =>
        """
        CertConvert — sample files for App Review
        =========================================

        Fictional test data (example.com) so you can exercise every feature.
        Nothing here is real or sensitive.

        Password for the encrypted key and the PFX:  certconvert

        Inspect tab — open any file:
          device-cert.pem / .cer / .p7b   a certificate in three formats
          device.pfx                      certificate + private key (password above)
          request.csr                     a certificate signing request
          device-key.pem                  a private key

        Convert tab — load a certificate and export another format:
          e.g. open device-cert.pem, choose PKCS #12 (.pfx), set a password, save.

        Chain tab — add these three (in any order), then Validate Chain:
          root-ca.pem, intermediate-ca.pem, server.pem
          (or open chain-bundle.pem, which contains all three shuffled)

        Keys tab:
          Convert device-key.pem between formats, or use
          device-key-encrypted.pem (password above). Check Match with
          device-cert.pem to confirm the key belongs to the certificate.

        Generate tab needs no input — it creates keys, CSRs and self-signed
        certificates directly.
        """;
}

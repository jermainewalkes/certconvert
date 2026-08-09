using System.IO;
using System.Security.Cryptography.X509Certificates;
using CertConvert.Cli;
using CertConvert.Core;

namespace CertConvert.Gui.Tests;

/// <summary>Exercises the `ca sign` CLI verb over the Core signer (Commands.CaSign is
/// internal, visible here via InternalsVisibleTo).</summary>
public class CliCaSignTests
{
    private sealed record Setup(string Dir, string CaCert, string CaKey, string Csr, string Out);

    private static Setup Prepare(bool caMarkedAsCa = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        using var caKey = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        using var caCert = Generator.CreateSelfSigned(caKey.Key, new CertSpec
        {
            CommonName = "Test CA", IsCertificateAuthority = caMarkedAsCa, ValidityDays = 3650,
        });
        var caCertPath = Path.Combine(dir, "ca.pem");
        var caKeyPath = Path.Combine(dir, "ca.key");
        File.WriteAllText(caCertPath, caCert.ExportCertificatePem() + "\n");
        File.WriteAllText(caKeyPath, KeyTools.ExportPem(caKey.Key, KeyOutputFormat.Pkcs8Pem));

        using var devKey = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        var csrPath = Path.Combine(dir, "device.csr");
        File.WriteAllText(csrPath, Generator.CreateCsrPem(devKey.Key, new CertSpec
        {
            CommonName = "device.test", DnsNames = ["device.test"],
        }));

        return new Setup(dir, caCertPath, caKeyPath, csrPath, Path.Combine(dir, "issued.pem"));
    }

    [Fact]
    public void CaSign_Issues_ReturnsOk()
    {
        var s = Prepare();
        try
        {
            int rc = Commands.CaSign(new[]
            {
                "--csr", s.Csr, "--ca-cert", s.CaCert, "--ca-key", s.CaKey,
                "--dns", "device.test", "-o", s.Out,
            });
            Assert.Equal(0, rc);
            Assert.True(File.Exists(s.Out));

            using var content = ContentLoader.LoadFile(s.Out);
            var info = Inspector.Inspect(content.Certificates[0]);
            Assert.Equal("device.test", info.DisplayName);
            Assert.False(info.IsCertificateAuthority);              // end-entity
            Assert.Contains("DNS:device.test", info.SubjectAlternativeNames);
        }
        finally { Directory.Delete(s.Dir, true); }
    }

    [Fact]
    public void CaSign_AcceptsBinaryDerCsr()
    {
        var s = Prepare();
        try
        {
            using var devKey = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
            byte[] der = new CertificateRequest("CN=der.test", (System.Security.Cryptography.ECDsa)devKey.Key,
                System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSigningRequest();
            var derCsr = Path.Combine(s.Dir, "device.der");
            File.WriteAllBytes(derCsr, der);

            int rc = Commands.CaSign(new[]
            {
                "--csr", derCsr, "--ca-cert", s.CaCert, "--ca-key", s.CaKey,
                "--dns", "der.test", "-o", s.Out,
            });
            Assert.Equal(0, rc);
            Assert.True(File.Exists(s.Out));
        }
        finally { Directory.Delete(s.Dir, true); }
    }

    [Fact]
    public void CaSign_PicksMatchingCertFromMultiCertCaFile()
    {
        var s = Prepare();
        try
        {
            // Prepend an unrelated CA so the real signing cert is NOT first in the --ca-cert file;
            // the key must still be matched to the right certificate (not assumed to be first).
            using var otherKey = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
            using var other = Generator.CreateSelfSigned(otherKey.Key, new CertSpec
            {
                CommonName = "Unrelated CA", IsCertificateAuthority = true, ValidityDays = 3650,
            });
            File.WriteAllText(s.CaCert, other.ExportCertificatePem() + "\n" + File.ReadAllText(s.CaCert));

            int rc = Commands.CaSign(new[]
            {
                "--csr", s.Csr, "--ca-cert", s.CaCert, "--ca-key", s.CaKey,
                "--dns", "device.test", "-o", s.Out,
            });
            Assert.Equal(0, rc);

            using var content = ContentLoader.LoadFile(s.Out);
            var info = Inspector.Inspect(content.Certificates[0]);
            Assert.Contains("Test CA", info.Issuer);            // issued by the matching CA
            Assert.DoesNotContain("Unrelated", info.Issuer);    // not the first, unrelated cert
        }
        finally { Directory.Delete(s.Dir, true); }
    }

    [Fact]
    public void CaSign_NonCaIssuer_ThrowsWithoutForce_SucceedsWithForce()
    {
        var s = Prepare(caMarkedAsCa: false);   // a self-signed leaf, not a CA
        try
        {
            Assert.Throws<CertConvertException>(() => Commands.CaSign(new[]
            {
                "--csr", s.Csr, "--ca-cert", s.CaCert, "--ca-key", s.CaKey,
                "--dns", "device.test", "-o", s.Out,
            }));

            int rc = Commands.CaSign(new[]
            {
                "--csr", s.Csr, "--ca-cert", s.CaCert, "--ca-key", s.CaKey,
                "--dns", "device.test", "--force", "-o", s.Out,
            });
            Assert.Equal(0, rc);
            Assert.True(File.Exists(s.Out));
        }
        finally { Directory.Delete(s.Dir, true); }
    }
}

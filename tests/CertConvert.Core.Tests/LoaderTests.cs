using System.Text;

namespace CertConvert.Core.Tests;

public class LoaderTests
{
    [Fact]
    public void Garbage_IsRejectedWithClearError()
    {
        var e = Assert.Throws<UnrecognisedContentException>(
            () => ContentLoader.Load(Encoding.ASCII.GetBytes("not a certificate at all")));
        Assert.Contains("not a recognisable", e.Message);
    }

    [Fact]
    public void RandomBinary_IsRejected()
    {
        Assert.Throws<UnrecognisedContentException>(
            () => ContentLoader.Load([0x30, 0x82, 0x01, 0x00, 0xFF, 0xFF]));
    }

    [Fact]
    public void PemWithUnknownLabelOnly_IsRejectedListingLabel()
    {
        var pem = "-----BEGIN WIDGET-----\nAAAA\n-----END WIDGET-----\n";
        var e = Assert.Throws<UnrecognisedContentException>(
            () => ContentLoader.Load(Encoding.ASCII.GetBytes(pem)));
        Assert.Contains("WIDGET", e.Message);
    }

    [Fact]
    public void CombinedPem_CertPlusKey_LoadsBoth()
    {
        using var chain = TestChain.Create();
        string pem = chain.Leaf.ExportCertificatePem() + "\n" +
                     KeyTools.ExportPem(chain.LeafKey, KeyOutputFormat.Pkcs8Pem);

        using var loaded = ContentLoader.Load(Encoding.UTF8.GetBytes(pem));
        Assert.Equal(ContentKind.Certificates, loaded.Kind);
        Assert.Single(loaded.Certificates);
        Assert.Single(loaded.PrivateKeys);
        Assert.Contains("1 certificate", loaded.SourceDescription);
        Assert.Contains("1 private key", loaded.SourceDescription);
    }

    [Fact]
    public void PemWithWindowsLineEndings_Loads()
    {
        using var chain = TestChain.Create();
        string pem = chain.Leaf.ExportCertificatePem().Replace("\n", "\r\n");
        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pem));
        Assert.Single(loaded.Certificates);
    }

    [Fact]
    public void DerPkcs8Key_IsDetected()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        byte[] der = KeyTools.ExportDer(entry.Key);
        using var loaded = ContentLoader.Load(der);
        Assert.Equal(ContentKind.PrivateKey, loaded.Kind);
    }

    [Fact]
    public void FileErrors_NameTheOffendingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "mystery-input.bin");
        File.WriteAllText(path, "definitely not a certificate");
        try
        {
            var e = Assert.Throws<UnrecognisedContentException>(
                () => ContentLoader.LoadFile(path));
            Assert.StartsWith("mystery-input.bin:", e.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PasswordErrors_NameTheOffendingFile()
    {
        using var chain = TestChain.Create();
        var path = Path.Combine(Path.GetTempPath(), "locked.pfx");
        File.WriteAllBytes(path, Converter.Export([chain.Leaf], CertOutputFormat.Pkcs12,
            new ExportOptions { Password = "secret" }));
        try
        {
            var e = Assert.Throws<PasswordRequiredException>(
                () => ContentLoader.LoadFile(path));
            Assert.StartsWith("locked.pfx:", e.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OversizeFile_IsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using (var fs = File.Create(path))
            fs.SetLength(11 * 1024 * 1024);
        try
        {
            var e = Assert.Throws<UnrecognisedContentException>(
                () => ContentLoader.LoadFile(path));
            Assert.Contains("10 MB", e.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemoveDuplicates_KeepsFirstOccurrenceInOrder()
    {
        using var chain = TestChain.Create();
        var leafCopy = System.Security.Cryptography.X509Certificates
            .X509CertificateLoader.LoadCertificate(chain.Leaf.RawData);
        var list = new List<System.Security.Cryptography.X509Certificates.X509Certificate2>
        {
            chain.Leaf, chain.Root, leafCopy, chain.Intermediate,
        };

        Converter.RemoveDuplicates(list);

        Assert.Equal(
            new[] { chain.Leaf.Thumbprint, chain.Root.Thumbprint, chain.Intermediate.Thumbprint },
            list.Select(c => c.Thumbprint));
    }

    [Fact]
    public void EmptyFile_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, []);
        try
        {
            Assert.Throws<UnrecognisedContentException>(() => ContentLoader.LoadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EncryptedPemWithBadDekInfoIv_IsRejectedCleanly()
    {
        // A hand-edited encrypted PEM with a non-hex DEK-Info IV must produce a
        // clean error, not a FormatException crash.
        var pem = "-----BEGIN RSA PRIVATE KEY-----\n" +
                  "Proc-Type: 4,ENCRYPTED\n" +
                  "DEK-Info: AES-256-CBC,XYZ\n\n" +
                  "AAAA\n" +
                  "-----END RSA PRIVATE KEY-----\n";
        Assert.Throws<UnrecognisedContentException>(
            () => ContentLoader.Load(Encoding.ASCII.GetBytes(pem), "pass"));
    }

    [Fact]
    public void EncryptedPemWithWrongLengthIv_IsRejectedCleanly()
    {
        // AES-256-CBC needs a 16-byte IV; supply 8 bytes (valid hex, wrong length).
        var pem = "-----BEGIN RSA PRIVATE KEY-----\n" +
                  "Proc-Type: 4,ENCRYPTED\n" +
                  "DEK-Info: AES-256-CBC,0011223344556677\n\n" +
                  "AAAA\n" +
                  "-----END RSA PRIVATE KEY-----\n";
        var e = Assert.Throws<UnrecognisedContentException>(
            () => ContentLoader.Load(Encoding.ASCII.GetBytes(pem), "pass"));
        Assert.Contains("16 bytes", e.Message);
    }
}

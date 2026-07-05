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
}

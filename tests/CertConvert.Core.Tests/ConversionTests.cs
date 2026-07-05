using System.Text;

namespace CertConvert.Core.Tests;

public class ConversionTests
{
    [Fact]
    public void PemBundle_RoundTrips_AllCertificates()
    {
        using var chain = TestChain.Create();
        byte[] pem = Converter.Export(chain.All, CertOutputFormat.Pem);

        using var loaded = ContentLoader.Load(pem);
        Assert.Equal(ContentKind.Certificates, loaded.Kind);
        Assert.Equal(3, loaded.Certificates.Count);
        Assert.Equal(
            chain.All.Select(c => c.Thumbprint).Order(),
            loaded.Certificates.Select(c => c.Thumbprint).Order());
    }

    [Fact]
    public void Der_RoundTrips_SingleCertificate()
    {
        using var chain = TestChain.Create();
        byte[] der = Converter.Export([chain.Leaf], CertOutputFormat.Der);

        using var loaded = ContentLoader.Load(der);
        Assert.Equal(ContentKind.Certificates, loaded.Kind);
        Assert.Equal(chain.Leaf.Thumbprint, Assert.Single(loaded.Certificates).Thumbprint);
    }

    [Fact]
    public void Der_RejectsMultipleCertificates()
    {
        using var chain = TestChain.Create();
        var e = Assert.Throws<CertConvertException>(
            () => Converter.Export(chain.All, CertOutputFormat.Der));
        Assert.Contains("PKCS #7", e.Message);
    }

    [Theory]
    [InlineData(CertOutputFormat.Pkcs7Der)]
    [InlineData(CertOutputFormat.Pkcs7Pem)]
    public void Pkcs7_RoundTrips_WholeChain(CertOutputFormat format)
    {
        using var chain = TestChain.Create();
        byte[] p7 = Converter.Export(chain.All, format);

        using var loaded = ContentLoader.Load(p7);
        Assert.Equal(ContentKind.Certificates, loaded.Kind);
        Assert.Equal(
            chain.All.Select(c => c.Thumbprint).Order(),
            loaded.Certificates.Select(c => c.Thumbprint).Order());
    }

    [Theory]
    [InlineData(Pkcs12Encryption.Modern)]
    [InlineData(Pkcs12Encryption.Legacy)]
    public void Pkcs12_RoundTrips_ChainAndKey(Pkcs12Encryption encryption)
    {
        using var chain = TestChain.Create();
        byte[] pfx = Converter.Export(chain.All, CertOutputFormat.Pkcs12, new ExportOptions
        {
            Password = "test-password",
            PrivateKey = chain.LeafKey,
            Encryption = encryption,
        });

        using var loaded = ContentLoader.Load(pfx, "test-password");
        Assert.Equal(ContentKind.Pkcs12, loaded.Kind);
        Assert.Equal(3, loaded.Certificates.Count);
        var key = Assert.Single(loaded.PrivateKeys);
        var leaf = loaded.Certificates.Single(c => c.Thumbprint == chain.Leaf.Thumbprint);
        Assert.True(KeyTools.Matches(leaf, key.Key));
    }

    [Fact]
    public void Pkcs12_WrongPassword_Throws()
    {
        using var chain = TestChain.Create();
        byte[] pfx = Converter.Export([chain.Leaf], CertOutputFormat.Pkcs12,
            new ExportOptions { Password = "correct" });

        Assert.Throws<InvalidPasswordException>(() => ContentLoader.Load(pfx, "wrong"));
    }

    [Fact]
    public void Pkcs12_MissingPassword_SignalsPasswordRequired()
    {
        using var chain = TestChain.Create();
        byte[] pfx = Converter.Export([chain.Leaf], CertOutputFormat.Pkcs12,
            new ExportOptions { Password = "secret" });

        Assert.Throws<PasswordRequiredException>(() => ContentLoader.Load(pfx, null));
    }

    [Fact]
    public void Pkcs12_MismatchedKey_IsRejected()
    {
        using var chain = TestChain.Create();
        var e = Assert.Throws<CertConvertException>(() =>
            Converter.Export([chain.Root], CertOutputFormat.Pkcs12, new ExportOptions
            {
                Password = "x",
                PrivateKey = chain.LeafKey,
            }));
        Assert.Contains("does not match", e.Message);
    }

    [Fact]
    public void PemOutput_IsAsciiArmoured()
    {
        using var chain = TestChain.Create();
        string pem = Encoding.ASCII.GetString(
            Converter.Export([chain.Leaf], CertOutputFormat.Pem));
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        Assert.Contains("-----END CERTIFICATE-----", pem);
    }

    [Fact]
    public void GuessFormat_CoversCommonExtensions()
    {
        Assert.Equal(CertOutputFormat.Pem, Converter.GuessFormatFromExtension("a.pem"));
        Assert.Equal(CertOutputFormat.Pem, Converter.GuessFormatFromExtension("a.crt"));
        Assert.Equal(CertOutputFormat.Der, Converter.GuessFormatFromExtension("a.cer"));
        Assert.Equal(CertOutputFormat.Der, Converter.GuessFormatFromExtension("a.der"));
        Assert.Equal(CertOutputFormat.Pkcs7Der, Converter.GuessFormatFromExtension("a.p7b"));
        Assert.Equal(CertOutputFormat.Pkcs12, Converter.GuessFormatFromExtension("a.pfx"));
        Assert.Equal(CertOutputFormat.Pkcs12, Converter.GuessFormatFromExtension("a.p12"));
        Assert.Null(Converter.GuessFormatFromExtension("a.txt"));
    }
}

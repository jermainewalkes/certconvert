using System.Text;

namespace CertConvert.Core.Tests;

public class GeneratorTests
{
    private static readonly CertSpec Spec = new()
    {
        CommonName = "unit.test.local",
        Organization = "CertConvert Tests",
        Country = "GB",
        DnsNames = ["unit.test.local", "alt.test.local"],
        IpAddresses = ["10.0.0.36"],
        ValidityDays = 90,
    };

    [Fact]
    public void SelfSigned_ContainsRequestedFields()
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        using var cert = Generator.CreateSelfSigned(key.Key, Spec);
        var info = Inspector.Inspect(cert);

        Assert.Contains("CN=unit.test.local", info.Subject);
        Assert.Contains("O=CertConvert Tests", info.Subject);
        Assert.True(info.IsSelfSigned);
        Assert.False(info.IsCertificateAuthority);
        Assert.Contains("DNS:unit.test.local", info.SubjectAlternativeNames);
        Assert.Contains("DNS:alt.test.local", info.SubjectAlternativeNames);
        Assert.Contains("IP:10.0.0.36", info.SubjectAlternativeNames);
        Assert.False(info.IsExpired);
        Assert.NotNull(info.SubjectKeyIdentifier);
    }

    [Fact]
    public void SelfSignedCa_IsMarkedAsCa()
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        using var cert = Generator.CreateSelfSigned(
            key.Key, Spec with { IsCertificateAuthority = true, DnsNames = [], IpAddresses = [] });
        var info = Inspector.Inspect(cert);

        Assert.True(info.IsCertificateAuthority);
        Assert.Contains("KeyCertSign", info.KeyUsages);
    }

    [Fact]
    public void Csr_IsWellFormedPem()
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        string csr = Generator.CreateCsrPem(key.Key, Spec);
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", csr);

        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(csr));
        Assert.Equal(ContentKind.CertificateRequest, loaded.Kind);
        Assert.NotNull(loaded.CertificateRequestPem);
    }

    [Theory]
    [InlineData(KeyAlgorithmChoice.Rsa2048, "RSA", 2048)]
    [InlineData(KeyAlgorithmChoice.Rsa4096, "RSA", 4096)]
    [InlineData(KeyAlgorithmChoice.EcP256, "ECDSA", 256)]
    [InlineData(KeyAlgorithmChoice.EcP521, "ECDSA", 521)]
    public void CreateKey_ProducesRequestedAlgorithm(
        KeyAlgorithmChoice choice, string algorithm, int size)
    {
        using var entry = Generator.CreateKey(choice);
        Assert.Equal(algorithm, entry.Algorithm);
        Assert.Equal(size, entry.KeySize);
    }

    [Fact]
    public void MissingCommonName_IsRejected()
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        Assert.Throws<CertConvertException>(
            () => Generator.CreateSelfSigned(key.Key, Spec with { CommonName = " " }));
    }

    [Fact]
    public void BadCountryCode_IsRejected()
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        Assert.Throws<CertConvertException>(
            () => Generator.CreateSelfSigned(key.Key, Spec with { Country = "GBR" }));
    }

    [Fact]
    public void BadIpAddress_IsRejected()
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        Assert.Throws<CertConvertException>(
            () => Generator.CreateSelfSigned(key.Key, Spec with { IpAddresses = ["not-an-ip"] }));
    }
}

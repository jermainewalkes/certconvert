namespace CertConvert.Core.Tests;

/// <summary>Verifies we can read what OpenSSL writes (fixtures generated with OpenSSL 3.6).</summary>
public class InteropTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("legacy_rsa_des3.pem")]
    [InlineData("legacy_rsa_aes256.pem")]
    public void LegacyTraditionalEncryptedPem_Loads(string file)
    {
        using var loaded = ContentLoader.LoadFile(Fixture(file), "testpass");
        var key = Assert.Single(loaded.PrivateKeys);
        Assert.Equal("RSA", key.Algorithm);
        Assert.Equal(2048, key.KeySize);
    }

    [Fact]
    public void LegacyTraditionalEncryptedPem_WrongPassword_Throws()
    {
        Assert.Throws<InvalidPasswordException>(
            () => ContentLoader.LoadFile(Fixture("legacy_rsa_des3.pem"), "wrong"));
    }

    [Fact]
    public void LegacyTraditionalEncryptedPem_NoPassword_SignalsPasswordRequired()
    {
        Assert.Throws<PasswordRequiredException>(
            () => ContentLoader.LoadFile(Fixture("legacy_rsa_des3.pem")));
    }

    [Fact]
    public void OpensslEncryptedPkcs8_Loads()
    {
        using var loaded = ContentLoader.LoadFile(
            Fixture("pkcs8_encrypted_aes256.pem"), "testpass");
        Assert.Equal("RSA", Assert.Single(loaded.PrivateKeys).Algorithm);
    }

    [Fact]
    public void OpensslP7b_Loads()
    {
        using var loaded = ContentLoader.LoadFile(Fixture("ossl_bundle.p7b"));
        Assert.Equal(ContentKind.Certificates, loaded.Kind);
        var cert = Assert.Single(loaded.Certificates);
        Assert.Contains("OpenSSL Interop Test", cert.Subject);
    }

    [Theory]
    [InlineData("ossl_modern.pfx")]
    [InlineData("ossl_3des.pfx")]
    public void OpensslPfx_LoadsCertAndKey(string file)
    {
        using var loaded = ContentLoader.LoadFile(Fixture(file), "testpass");
        Assert.Equal(ContentKind.Pkcs12, loaded.Kind);
        var cert = Assert.Single(loaded.Certificates);
        var key = Assert.Single(loaded.PrivateKeys);
        Assert.True(KeyTools.Matches(cert, key.Key));
    }

    [Fact]
    public void OpensslKeyAndCert_Match()
    {
        using var certContent = ContentLoader.LoadFile(Fixture("ossl_cert.pem"));
        using var keyContent = ContentLoader.LoadFile(Fixture("ossl_key.pem"));
        Assert.True(KeyTools.Matches(
            certContent.Certificates[0], keyContent.PrivateKeys[0].Key));
    }
}

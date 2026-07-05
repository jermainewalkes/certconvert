using System.Security.Cryptography;
using System.Text;

namespace CertConvert.Core.Tests;

public class KeyTests
{
    [Fact]
    public void Pkcs8Pem_RoundTrips_Rsa()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        string pem = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8Pem);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", pem);

        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pem));
        Assert.Equal(ContentKind.PrivateKey, loaded.Kind);
        var reloaded = Assert.Single(loaded.PrivateKeys);
        Assert.Equal("RSA", reloaded.Algorithm);
        Assert.Equal(2048, reloaded.KeySize);
    }

    [Fact]
    public void Pkcs1Pem_RoundTrips_Rsa()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        string pem = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs1Pem);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", pem);

        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pem));
        Assert.Equal("RSA", Assert.Single(loaded.PrivateKeys).Algorithm);
    }

    [Fact]
    public void Sec1Pem_RoundTrips_Ec()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        string pem = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Sec1Pem);
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", pem);

        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pem));
        var reloaded = Assert.Single(loaded.PrivateKeys);
        Assert.Equal("ECDSA", reloaded.Algorithm);
        Assert.Equal(256, reloaded.KeySize);
    }

    [Fact]
    public void EncryptedPkcs8_RoundTrips_WithPassword()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.EcP384);
        string pem = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8EncryptedPem, "pass123");
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", pem);

        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pem), "pass123");
        Assert.Equal("ECDSA", Assert.Single(loaded.PrivateKeys).Algorithm);
    }

    [Fact]
    public void EncryptedPkcs8_WrongPassword_Throws()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        string pem = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8EncryptedPem, "right");
        Assert.Throws<InvalidPasswordException>(
            () => ContentLoader.Load(Encoding.ASCII.GetBytes(pem), "wrong"));
    }

    [Fact]
    public void EncryptedPkcs8_NoPassword_SignalsPasswordRequired()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        string pem = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8EncryptedPem, "secret");
        Assert.Throws<PasswordRequiredException>(
            () => ContentLoader.Load(Encoding.ASCII.GetBytes(pem), null));
    }

    [Fact]
    public void Pkcs1_ToPkcs8_Conversion_PreservesKey()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.Rsa2048);
        string pkcs1 = KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs1Pem);

        using var loaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pkcs1));
        string pkcs8 = KeyTools.ExportPem(loaded.PrivateKeys[0].Key, KeyOutputFormat.Pkcs8Pem);

        using var reloaded = ContentLoader.Load(Encoding.ASCII.GetBytes(pkcs8));
        var a = (RSA)entry.Key;
        var b = (RSA)reloaded.PrivateKeys[0].Key;
        Assert.Equal(
            a.ExportSubjectPublicKeyInfo(),
            b.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Matches_IdentifiesTheRightCertificate()
    {
        using var chain = TestChain.Create();
        Assert.True(KeyTools.Matches(chain.Leaf, chain.LeafKey));
        Assert.False(KeyTools.Matches(chain.Root, chain.LeafKey));
    }

    [Fact]
    public void Pkcs1Export_RejectsEcKeys()
    {
        using var entry = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        Assert.Throws<CertConvertException>(
            () => KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs1Pem));
    }
}

using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core.Tests;

public class CsrTests
{
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    private const string Sha256WithRsa = "1.2.840.113549.1.1.11";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Builds an in-code RSA request with SANs, key usage, EKU and CA basic constraints.</summary>
    private static (byte[] Der, RSA Key) MakeRsaCsr()
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=in.code.test, O=Test Ltd, C=GB"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("in.code.test");
        san.AddIpAddress(System.Net.IPAddress.Parse("10.1.2.3"));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        return (req.CreateSigningRequest(), key);
    }

    [Fact]
    public void Inspect_ReadsRequestedFields()
    {
        var (der, key) = MakeRsaCsr();
        key.Dispose();
        var info = CsrInspector.Inspect(der);

        Assert.Equal("in.code.test", info.DisplayName);
        Assert.Contains("CN=in.code.test", info.Subject);
        Assert.Equal("RSA", info.PublicKeyAlgorithm);
        Assert.Equal(2048, info.PublicKeyBits);
        Assert.Contains("DNS:in.code.test", info.SubjectAlternativeNames);
        Assert.Contains("IP:10.1.2.3", info.SubjectAlternativeNames);
        Assert.Contains("DigitalSignature", info.KeyUsages);
        Assert.NotEmpty(info.EnhancedKeyUsages);
        Assert.Contains("1.3.6.1.5.5.7.3.1", info.EnhancedKeyUsageOids);
        Assert.Equal(Sha256WithRsa, info.SignatureAlgorithmOid);
        Assert.Equal(CsrSignatureState.Valid, info.SignatureState);
        Assert.Empty(info.UnreadableExtensions);
    }

    [Fact]
    public void Inspect_OpenSslDerFixture_ParsesForeignEncoding()
    {
        // Proof that UnsafeLoadCertificateExtensions reads extensions from a foreign
        // (OpenSSL) encoder, not just from .NET-produced requests.
        var info = CsrInspector.Inspect(File.ReadAllBytes(FixturePath("ossl_request.csr")));

        Assert.Equal("device.example.com", info.DisplayName);
        Assert.Equal("ECDSA", info.PublicKeyAlgorithm);
        Assert.Contains("DNS:device.example.com", info.SubjectAlternativeNames);
        Assert.Contains("DNS:iot.example.com", info.SubjectAlternativeNames);
        Assert.Contains("IP:10.0.0.5", info.SubjectAlternativeNames);
        Assert.Contains("DigitalSignature", info.KeyUsages);
        Assert.Equal(2, info.EnhancedKeyUsages.Count);
        Assert.Equal(EcdsaWithSha256, info.SignatureAlgorithmOid);
        Assert.Equal(CsrSignatureState.Valid, info.SignatureState);
    }

    [Fact]
    public void Inspect_TamperedSignature_ParsesButReportsInvalid()
    {
        var (der, key) = MakeRsaCsr();
        key.Dispose();
        // Flip a byte inside the trailing signature value — the DER stays
        // structurally sound, so the request still parses; only the self-signature
        // fails to verify.
        der[^1] ^= 0xFF;

        var info = CsrInspector.Inspect(der);
        Assert.Equal("in.code.test", info.DisplayName);          // fields still readable
        Assert.Equal(CsrSignatureState.Invalid, info.SignatureState);
    }

    [Fact]
    public void Inspect_Garbage_Throws()
    {
        Assert.Throws<UnrecognisedContentException>(
            () => CsrInspector.Inspect(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x2A }));
    }

    [Fact]
    public void Inspect_OversizedInput_IsRefusedNotDecoded()
    {
        // A hostile multi-megabyte "request" must be refused up front, not decoded.
        Assert.Throws<UnrecognisedContentException>(
            () => CsrInspector.Inspect(new byte[2 * 1024 * 1024]));
    }

    [Fact]
    public void TryLoadDer_DistinguishesCsrFromNoise()
    {
        var (der, key) = MakeRsaCsr();
        key.Dispose();
        Assert.True(CsrInspector.TryLoadDer(der, out var pem));
        Assert.Contains("BEGIN CERTIFICATE REQUEST", pem);

        Assert.False(CsrInspector.TryLoadDer(new byte[] { 1, 2, 3, 4 }, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void ContentLoader_DetectsDerCsr()
    {
        using var content = ContentLoader.LoadFile(FixturePath("ossl_request.csr"));
        Assert.Equal(ContentKind.CertificateRequest, content.Kind);
        Assert.Single(content.CertificateRequests);
        Assert.Empty(content.Certificates);
        Assert.Contains("certificate request", content.SourceDescription);
    }

    [Fact]
    public void ContentLoader_KeepsEveryPemCsr()
    {
        var (der1, k1) = MakeRsaCsr();
        var (der2, k2) = MakeRsaCsr();
        k1.Dispose();
        k2.Dispose();
        string pem = PemUtil.Encode("CERTIFICATE REQUEST", der1) +
                     PemUtil.Encode("CERTIFICATE REQUEST", der2);

        using var content = ContentLoader.Load(System.Text.Encoding.UTF8.GetBytes(pem));
        Assert.Equal(2, content.CertificateRequests.Count);
        Assert.Contains("2 certificate requests", content.SourceDescription);
    }

    [Fact]
    public void MatchesAnyRequest_SkipsUnparseableBlocks()
    {
        using var key = RSA.Create(2048);
        string good = PemUtil.Encode("CERTIFICATE REQUEST", new CertificateRequest(
            "CN=good", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest());
        string bad = PemUtil.Encode("CERTIFICATE REQUEST", new byte[] { 1, 2, 3, 4 });

        // A corrupt request block ahead of the good one must not abort the search.
        Assert.True(KeyTools.MatchesAnyRequest(new[] { bad, good }, key));
        using var other = RSA.Create(2048);
        Assert.False(KeyTools.MatchesAnyRequest(new[] { bad }, other));
    }

    [Fact]
    public void DisplayName_HandlesCommaInsideCommonName()
    {
        // A comma inside the CN value must not split the name (proper DN parsing,
        // not a naive comma split). Build the DN via the builder so the comma is
        // correctly encoded in the value.
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName("Acme, Inc");
        builder.AddOrganizationName("Acme Ltd");
        builder.AddCountryOrRegion("GB");

        using var key = RSA.Create(2048);
        var req = new CertificateRequest(builder.Build(), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var info = CsrInspector.Inspect(req.CreateSigningRequest());
        Assert.Equal("Acme, Inc", info.DisplayName);
    }

    [Fact]
    public void KeyMatch_FindsRequestEvenWhenAnUnrelatedCertIsPresent()
    {
        // A file holding an unrelated certificate AND a request; the key matches the
        // request. The match must not stop at the certificate (the else-if bug).
        var now = DateTimeOffset.UtcNow;
        using var certKey = RSA.Create(2048);
        using var unrelated = new CertificateRequest("CN=unrelated", certKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(365));
        using var reqKey = RSA.Create(2048);
        var csrDer = new CertificateRequest("CN=req", reqKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();

        string pem = unrelated.ExportCertificatePem() + "\n" +
                     PemUtil.Encode("CERTIFICATE REQUEST", csrDer);
        using var content = ContentLoader.Load(System.Text.Encoding.UTF8.GetBytes(pem));
        Assert.Single(content.Certificates);
        Assert.Single(content.CertificateRequests);

        Assert.False(content.Certificates.Any(c => KeyTools.Matches(c, reqKey)));
        Assert.True(content.CertificateRequests.Any(
            p => KeyTools.Matches(CsrInspector.InspectPem(p), reqKey)));
    }

    [Fact]
    public void KeyMatchesRequest()
    {
        var csr = CsrInspector.Inspect(File.ReadAllBytes(FixturePath("ossl_request.csr")));

        using var keyContent = ContentLoader.LoadFile(FixturePath("ossl_request_key.pem"));
        Assert.True(KeyTools.Matches(csr, keyContent.PrivateKeys[0].Key));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(KeyTools.Matches(csr, other));
    }
}

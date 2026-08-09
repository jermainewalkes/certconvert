using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CertConvert.Core.Tests;

public class CaSignerTests
{
    private const string SanOid = "2.5.29.17";

    // --- builders ---------------------------------------------------------------

    /// <summary>A self-signed CA, returned as the public cert (as loaded from a file)
    /// plus its key held separately — exactly how the app supplies them to the signer.</summary>
    private static (X509Certificate2 Cert, RSA Key) RsaCa(
        DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, bool markAsCa = true)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Root CA, O=Test, C=GB", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (markAsCa)
        {
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        }
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var withKey = req.CreateSelfSigned(notBefore ?? now.AddDays(-1), notAfter ?? now.AddYears(10));
        return (X509CertificateLoader.LoadCertificate(withKey.RawData), key);
    }

    private static (X509Certificate2 Cert, ECDsa Key) EcCa()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Test EC Root CA", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var withKey = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return (X509CertificateLoader.LoadCertificate(withKey.RawData), key);
    }

    private static byte[] Csr(AsymmetricAlgorithm key, string subject = "CN=device.example.com", bool requestCa = false)
    {
        CertificateRequest req = key switch
        {
            RSA r => new CertificateRequest(subject, r, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa e => new CertificateRequest(subject, e, HashAlgorithmName.SHA256),
            _ => throw new InvalidOperationException(),
        };
        if (requestCa)
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSigningRequest();
    }

    private static X509Extension SanExtension(X509Certificate2 cert) =>
        cert.Extensions.First(e => e.Oid?.Value == SanOid);

    // --- happy path + chain -----------------------------------------------------

    [Fact]
    public void IssuedCertificate_ChainsToTheCa()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        using var issued = CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { DnsNames = ["device.example.com"] });

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        bool built = chain.Build(issued);
        Assert.True(built, string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())));

        ca.Dispose(); caKey.Dispose();
    }

    // --- extension policy -------------------------------------------------------

    [Fact]
    public void RequestedCaTrue_IsIgnored_IssuedIsEndEntity()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        using var issued = CaSigner.Sign(ca, caKey, Csr(reqKey, requestCa: true),
            new IssuanceOptions { DnsNames = ["device.example.com"] });

        Assert.False(Inspector.Inspect(issued).IsCertificateAuthority);   // CA:FALSE despite the request
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void KeyUsage_DependsOnIssuedKeyType()
    {
        var (ca, caKey) = RsaCa();
        using var rsaReq = RSA.Create(2048);
        using var issuedRsa = CaSigner.Sign(ca, caKey, Csr(rsaReq),
            new IssuanceOptions { Role = IssuedRole.TlsServer, DnsNames = ["x.test"] });
        var kuRsa = Inspector.Inspect(issuedRsa).KeyUsageFlags;
        Assert.True(kuRsa.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.True(kuRsa.HasFlag(X509KeyUsageFlags.KeyEncipherment));    // RSA server: present

        using var ecReq = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var issuedEc = CaSigner.Sign(ca, caKey, Csr(ecReq),
            new IssuanceOptions { Role = IssuedRole.TlsServer, DnsNames = ["x.test"] });
        var kuEc = Inspector.Inspect(issuedEc).KeyUsageFlags;
        Assert.True(kuEc.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.False(kuEc.HasFlag(X509KeyUsageFlags.KeyEncipherment));    // ECDSA: never

        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void ClientRole_CarriesEmailAndUriSans_AndClientAuthEku()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        using var issued = CaSigner.Sign(ca, caKey, Csr(reqKey, subject: "CN=user"),
            new IssuanceOptions
            {
                Role = IssuedRole.TlsClient,
                EmailNames = ["user@example.com"],
                Uris = ["spiffe://cluster/ns/sa"],
            });

        var raw = Encoding.ASCII.GetString(SanExtension(issued).RawData);
        Assert.Contains("user@example.com", raw);
        Assert.Contains("spiffe://cluster/ns/sa", raw);
        Assert.Contains("1.3.6.1.5.5.7.3.2", Inspector.Inspect(issued).EnhancedKeyUsageOids); // clientAuth
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void Sans_AreCarriedDeduplicatedAndValidated()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        using var issued = CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { DnsNames = ["a.test", "a.test"], IpAddresses = ["10.0.0.1"] });
        var sans = Inspector.Inspect(issued).SubjectAlternativeNames;
        Assert.Contains("DNS:a.test", sans);
        Assert.Contains("IP:10.0.0.1", sans);
        Assert.Equal(1, sans.Count(s => s == "DNS:a.test"));             // de-duplicated

        Assert.Throws<CertConvertException>(() => CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { IpAddresses = ["not-an-ip"] }));       // bad IP rejected
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void EmptySubject_MakesSanCritical()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        var emptySubjectCsr = new CertificateRequest(new X500DistinguishedName(""), reqKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();

        using var issued = CaSigner.Sign(ca, caKey, emptySubjectCsr,
            new IssuanceOptions { DnsNames = ["a.test"] });
        Assert.True(SanExtension(issued).Critical);                       // RFC 5280

        using var normal = CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { DnsNames = ["a.test"] });
        Assert.False(SanExtension(normal).Critical);
        ca.Dispose(); caKey.Dispose();
    }

    // --- refusals ---------------------------------------------------------------

    [Fact]
    public void BadProofOfPossession_IsRefused()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        var csr = Csr(reqKey);
        csr[^1] ^= 0xFF;   // corrupt the signature
        Assert.Throws<CertConvertException>(() => CaSigner.Sign(ca, caKey, csr, new IssuanceOptions()));
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void MalformedCsr_IsRefused()
    {
        var (ca, caKey) = RsaCa();
        Assert.ThrowsAny<CertConvertException>(() =>
            CaSigner.Sign(ca, caKey, new byte[] { 0x30, 0x03, 0x02, 0x01, 0x2A }, new IssuanceOptions()));
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void MismatchedCaKey_IsRefused()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        Assert.Throws<CertConvertException>(() =>
            CaSigner.Sign(ca, wrongKey, Csr(reqKey), new IssuanceOptions()));
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void ExpiredCa_IsRefused_EvenWithOverride()
    {
        var now = DateTimeOffset.UtcNow;
        var (ca, caKey) = RsaCa(notBefore: now.AddDays(-10), notAfter: now.AddDays(-1));
        using var reqKey = RSA.Create(2048);
        Assert.Throws<CertConvertException>(() => CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { AllowNonCaIssuer = true }));           // expiry is not overridable
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void NonCaSigner_RefusedByDefault_AllowedWithOverride()
    {
        var (leaf, leafKey) = RsaCa(markAsCa: false);
        using var reqKey = RSA.Create(2048);
        Assert.Throws<CertConvertException>(() =>
            CaSigner.Sign(leaf, leafKey, Csr(reqKey), new IssuanceOptions()));   // refused

        using var issued = CaSigner.Sign(leaf, leafKey, Csr(reqKey),
            new IssuanceOptions { AllowNonCaIssuer = true, DnsNames = ["a.test"] });
        Assert.NotNull(issued);                                                  // allowed
        leaf.Dispose(); leafKey.Dispose();
    }

    // --- validity + serial ------------------------------------------------------

    [Fact]
    public void Validity_IsClampedToTheCa()
    {
        var caExpiry = DateTimeOffset.UtcNow.AddDays(200);
        var (ca, caKey) = RsaCa(notAfter: caExpiry);
        using var reqKey = RSA.Create(2048);
        using var issued = CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { ValidityDays = 36500, DnsNames = ["a.test"] });   // asked for 100 years

        var issuedNotAfter = issued.NotAfter.ToUniversalTime();
        Assert.True(issuedNotAfter <= caExpiry.UtcDateTime.AddSeconds(2));          // clamped to the CA
        Assert.True(issuedNotAfter < DateTimeOffset.UtcNow.AddDays(400));           // not the requested 100y
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void Serial_IsPositiveNonZeroAndUnique()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        using var a = CaSigner.Sign(ca, caKey, Csr(reqKey), new IssuanceOptions { DnsNames = ["a.test"] });
        using var b = CaSigner.Sign(ca, caKey, Csr(reqKey), new IssuanceOptions { DnsNames = ["a.test"] });

        Assert.NotEqual(a.SerialNumber, b.SerialNumber);                 // unique
        byte[] serial = a.GetSerialNumber();                            // little-endian: MSB last
        Assert.True(serial[^1] < 0x80);                                 // top bit clear -> positive
        Assert.Contains(serial, x => x != 0);                           // non-zero
        ca.Dispose(); caKey.Dispose();
    }

    // --- hash chosen from the CA key --------------------------------------------

    [Fact]
    public void SignatureHash_ComesFromTheCaKey()
    {
        using var reqKey = RSA.Create(2048);

        var (rsaCa, rsaKey) = RsaCa();
        using (var issued = CaSigner.Sign(rsaCa, rsaKey, Csr(reqKey), new IssuanceOptions { DnsNames = ["a.test"] }))
            Assert.Equal("1.2.840.113549.1.1.11", Inspector.Inspect(issued).SignatureAlgorithmOid); // sha256WithRSA
        rsaCa.Dispose(); rsaKey.Dispose();

        var (ecCa, ecKey) = EcCa();
        using (var issued = CaSigner.Sign(ecCa, ecKey, Csr(reqKey), new IssuanceOptions { DnsNames = ["a.test"] }))
            Assert.Equal("1.2.840.10045.4.3.2", Inspector.Inspect(issued).SignatureAlgorithmOid);   // ecdsa-with-SHA256
        ecCa.Dispose(); ecKey.Dispose();
    }

    // --- ratify-round hardening -------------------------------------------------

    [Fact]
    public void NonSha256Request_IsAccepted()
    {
        // A P-384 / SHA-384 request must verify (proof of possession) — not be rejected
        // by an assumed SHA-256.
        var (ca, caKey) = RsaCa();
        using var reqKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var csr = new CertificateRequest("CN=p384.test", reqKey, HashAlgorithmName.SHA384).CreateSigningRequest();
        using var issued = CaSigner.Sign(ca, caKey, csr, new IssuanceOptions { DnsNames = ["p384.test"] });
        Assert.Equal("p384.test", Inspector.Inspect(issued).DisplayName);
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void EmptySubjectWithNoSan_IsRefused()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        var emptyCsr = new CertificateRequest(new X500DistinguishedName(""), reqKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        Assert.Throws<CertConvertException>(() =>
            CaSigner.Sign(ca, caKey, emptyCsr, new IssuanceOptions()));   // no SANs either
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void ExtremeValidity_IsRejectedCleanly()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        Assert.Throws<CertConvertException>(() => CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { ValidityDays = int.MaxValue, DnsNames = ["a.test"] }));
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void InvalidDnsSan_IsRejected()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        Assert.Throws<CertConvertException>(() => CaSigner.Sign(ca, caKey, Csr(reqKey),
            new IssuanceOptions { DnsNames = ["has space"] }));
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void TlsServerWithoutSan_IsRefused_ClientIsFine()
    {
        var (ca, caKey) = RsaCa();
        using var reqKey = RSA.Create(2048);
        // Server with no DNS/IP SAN is worthless -> refused.
        Assert.Throws<CertConvertException>(() =>
            CaSigner.Sign(ca, caKey, Csr(reqKey), new IssuanceOptions { Role = IssuedRole.TlsServer }));
        // Client is identified by its subject, so no SAN is allowed.
        using var client = CaSigner.Sign(ca, caKey, Csr(reqKey), new IssuanceOptions { Role = IssuedRole.TlsClient });
        Assert.NotNull(client);
        ca.Dispose(); caKey.Dispose();
    }

    [Fact]
    public void IssuedNotBefore_NeverPredatesTheCa()
    {
        var now = DateTimeOffset.UtcNow;
        var (ca, caKey) = RsaCa(notBefore: now, notAfter: now.AddYears(5));   // CA just created
        using var reqKey = RSA.Create(2048);
        using var issued = CaSigner.Sign(ca, caKey, Csr(reqKey), new IssuanceOptions { DnsNames = ["a.test"] });
        // The -5 min backdating must be clamped up to the CA's notBefore.
        Assert.True(issued.NotBefore.ToUniversalTime() >= ca.NotBefore.ToUniversalTime().AddSeconds(-2));
        ca.Dispose(); caKey.Dispose();
    }
}

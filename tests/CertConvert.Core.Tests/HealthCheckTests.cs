using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core.Tests;

public class HealthCheckTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static CertificateInfo InspectFixture(string name)
    {
        using var content = ContentLoader.LoadFile(FixturePath(name));
        return Inspector.Inspect(content.Certificates[0]);
    }

    private static bool Has(IReadOnlyList<Finding> f, string id) => f.Any(x => x.Id == id);
    private static Finding? Get(IReadOnlyList<Finding> f, string id) => f.FirstOrDefault(x => x.Id == id);

    // ---- anti-false-alarm regression guards (the whole point of role awareness) --

    [Fact]
    public void HealthyLeaf_HasNoCriticalOrWarning()
    {
        using var chain = TestChain.Create();
        var findings = HealthCheck.Analyse(Inspector.Inspect(chain.Leaf));
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Critical);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void LongLivedPrivateRoot_IsNotFlaggedForValidityOrSan()
    {
        // The 15-year test root is correct practice — it must not be nagged the way
        // a public TLS certificate would be.
        using var chain = TestChain.Create();
        var findings = HealthCheck.Analyse(Inspector.Inspect(chain.Root));
        Assert.False(Has(findings, "long-validity"));
        Assert.False(Has(findings, "no-san"));
    }

    // ---- the CN-only case the feature exists to catch --------------------------

    [Fact]
    public void CnOnlyServerCertificate_FiresNoSan()
    {
        // No EKU, no SAN, host-name-shaped CN → treated as a TLS server cert, so the
        // missing SAN must surface (the trap the earlier role heuristic fell into).
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=www.example.com", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(400));

        var findings = HealthCheck.Analyse(Inspector.Inspect(cert));
        var noSan = Get(findings, "no-san");
        Assert.NotNull(noSan);
        Assert.Equal(Severity.Critical, noSan!.Severity);
        // A self-signed 400-day server cert should also flag long validity + self-signed.
        Assert.True(Has(findings, "long-validity"));
        Assert.True(Has(findings, "self-signed"));
    }

    // ---- key and signature strength --------------------------------------------

    [Fact]
    public void WeakRsaKey_IsCritical()
    {
        var findings = HealthCheck.Analyse(InspectFixture("weak_rsa1024_cert.pem"));
        var weak = Get(findings, "weak-rsa-key");
        Assert.NotNull(weak);
        Assert.Equal(Severity.Critical, weak!.Severity);
    }

    [Fact]
    public void Sha1OnLeaf_IsCritical()
    {
        var findings = HealthCheck.Analyse(InspectFixture("sha1_cert.pem"));
        var weak = Get(findings, "weak-signature");
        Assert.NotNull(weak);
        Assert.Equal(Severity.Critical, weak!.Severity);
    }

    [Fact]
    public void Sha1OnSelfSignedRoot_DowngradesToNotice()
    {
        // A root's own signature is never checked in path validation, so a weak hash
        // on it is a footnote, not a break.
        var findings = HealthCheck.Analyse(InspectFixture("sha1_selfsigned_root.pem"));
        var weak = Get(findings, "weak-signature");
        Assert.NotNull(weak);
        Assert.Equal(Severity.Notice, weak!.Severity);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Critical);
    }

    [Fact]
    public void Sha1OnSelfSignedLeaf_StaysCritical()
    {
        // Only a self-signed *root* earns the downgrade. A self-signed leaf is still
        // validated by whatever relies on it, so its weak signature is Critical.
        var findings = HealthCheck.Analyse(InspectFixture("sha1_selfsigned_leaf.pem"));
        var weak = Get(findings, "weak-signature");
        Assert.NotNull(weak);
        Assert.Equal(Severity.Critical, weak!.Severity);
    }

    [Fact]
    public void SelfIssuedButCrossSigned_IsNotTreatedAsSelfSigned()
    {
        // Subject DN == issuer DN, but signed by a *different* key. The weak-signature
        // downgrade must not fire for it — IsSelfSignatureValid is the gate.
        var now = DateTimeOffset.UtcNow;
        using var realKey = RSA.Create(2048);
        using var attackerKey = RSA.Create(2048);
        var req = new CertificateRequest("CN=Fake Root", realKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var generator = X509SignatureGenerator.CreateForRSA(attackerKey, RSASignaturePadding.Pkcs1);
        using var cert = req.Create(req.SubjectName, generator,
            now.AddDays(-1), now.AddDays(365), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var info = Inspector.Inspect(cert);
        Assert.True(info.IsSelfSigned);           // subject == issuer
        Assert.False(info.IsSelfSignatureValid);  // but the signature is another key's
    }

    [Fact]
    public void SelfSignatureValid_TrueForGenuineRoot_FalseForCaSignedLeaf()
    {
        Assert.True(InspectFixture("sha1_selfsigned_root.pem").IsSelfSignatureValid);
        Assert.False(InspectFixture("sha1_cert.pem").IsSelfSignatureValid);
    }

    [Fact]
    public void SelfIssuedButCrossSigned_GetsNoSelfSignedNotice()
    {
        var now = DateTimeOffset.UtcNow;
        using var realKey = RSA.Create(2048);
        using var attackerKey = RSA.Create(2048);
        var req = new CertificateRequest("CN=Fake Root", realKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var generator = X509SignatureGenerator.CreateForRSA(attackerKey, RSASignaturePadding.Pkcs1);
        using var cert = req.Create(req.SubjectName, generator,
            now.AddDays(-1), now.AddDays(365), new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 });

        var findings = HealthCheck.Analyse(Inspector.Inspect(cert));
        Assert.False(Has(findings, "self-signed")); // it does not truly vouch for itself
    }

    [Fact]
    public void SanOnlyServerCert_FiresLongValidity()
    {
        // No EKU, non-host-name CN, but a DNS SAN and a >398-day window → a TLS
        // server cert, so the excessive-validity check must fire.
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=internal-node", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("node.internal");
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(500));

        Assert.True(Has(HealthCheck.Analyse(Inspector.Inspect(cert)), "long-validity"));
    }

    [Fact]
    public void EmailCommonName_IsNotTreatedAsServer()
    {
        // An S/MIME-style CN (an email address) with no SAN must not be misread as a
        // server certificate and flagged with a false no-SAN error.
        var builder = new X500DistinguishedNameBuilder();
        builder.AddEmailAddress("user.name@example.com");
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(builder.Build(), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));

        Assert.False(Has(HealthCheck.Analyse(Inspector.Inspect(cert)), "no-san"));
    }

    [Fact]
    public void CaWithServerAuthEku_IsFlagged()
    {
        // A certificate authority that also claims TLS server-auth is a dangerous mix.
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Confused CA", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));

        var findings = HealthCheck.Analyse(Inspector.Inspect(cert));
        var confused = Get(findings, "ee-asserting-ca");
        Assert.NotNull(confused);
        Assert.Equal(Severity.Critical, confused!.Severity);
    }

    // ---- expiry (deterministic via injected clock) -----------------------------

    [Fact]
    public void Expiry_UsesInjectedClock()
    {
        using var chain = TestChain.Create(
            leafNotBefore: DateTimeOffset.UtcNow.AddDays(-400),
            leafNotAfter: DateTimeOffset.UtcNow.AddDays(-35));
        var info = Inspector.Inspect(chain.Leaf);

        var expired = HealthCheck.Analyse(info, DateTimeOffset.UtcNow);
        Assert.True(Has(expired, "expired"));

        // Rewind the clock to inside the window: not expired, but expiring soon.
        var mid = HealthCheck.Analyse(info, chain.Leaf.NotAfter.AddDays(-10));
        Assert.False(Has(mid, "expired"));
        Assert.True(Has(mid, "expiring-soon"));
    }

    // ---- CSR analysis ----------------------------------------------------------

    [Fact]
    public void CsrHealthCheck_CleanRequestHasNoFindings()
    {
        var csr = CsrInspector.Inspect(File.ReadAllBytes(FixturePath("ossl_request.csr")));
        var findings = HealthCheck.Analyse(csr);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Critical);
    }

    [Fact]
    public void ToText_ReadsCleanWhenHealthy()
    {
        Assert.Contains("no problems found", HealthCheck.ToText([]));
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core.Tests;

public class HostnameTests
{
    private static X509Certificate2 SelfSigned(string subject,
        string[]? dnsNames = null, string[]? ipAddresses = null)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (dnsNames is not null || ipAddresses is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var d in dnsNames ?? [])
                san.AddDnsName(d);
            foreach (var ip in ipAddresses ?? [])
                san.AddIpAddress(System.Net.IPAddress.Parse(ip));
            req.CertificateExtensions.Add(san.Build());
        }
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));
    }

    [Fact]
    public void ExactSanName_IsCovered()
    {
        using var cert = SelfSigned("CN=www.example.com", dnsNames: ["www.example.com"]);
        var r = HostnameCheck.Check(cert, "www.example.com");
        Assert.Equal(HostnameCoverage.Covered, r.Coverage);
        Assert.True(r.IsCovered);
    }

    [Fact]
    public void WildcardSan_IsReportedAsWildcard()
    {
        using var cert = SelfSigned("CN=*.example.com", dnsNames: ["*.example.com"]);
        var r = HostnameCheck.Check(cert, "api.example.com");
        Assert.Equal(HostnameCoverage.CoveredByWildcard, r.Coverage);
        Assert.True(r.IsCovered);
    }

    [Fact]
    public void WildcardCoversOneLabelOnly()
    {
        using var cert = SelfSigned("CN=*.example.com", dnsNames: ["*.example.com"]);
        // Two labels below the wildcard is not covered.
        Assert.False(HostnameCheck.Check(cert, "a.b.example.com").IsCovered);
        // The bare domain is not covered by *.example.com either.
        Assert.False(HostnameCheck.Check(cert, "example.com").IsCovered);
    }

    [Fact]
    public void CnOnly_IsNotCovered_AndSaysWhy()
    {
        // Name is in the common name but there is no SAN — the case people trip on.
        using var cert = SelfSigned("CN=legacy.example.com");
        var r = HostnameCheck.Check(cert, "legacy.example.com");
        Assert.Equal(HostnameCoverage.OnlyInCommonName, r.Coverage);
        Assert.False(r.IsCovered);
        Assert.Contains("common name", r.Explanation);
    }

    [Fact]
    public void HostInCommonNameWhileCertHasOtherSan_ReportsOnlyInCommonName()
    {
        // The cert has a SAN (foo), so MatchesHostname ignores the CN entirely.
        // The CN-only trap must still be explained via an explicit CN comparison.
        using var cert = SelfSigned("CN=bar.example.com", dnsNames: ["foo.example.com"]);
        var r = HostnameCheck.Check(cert, "bar.example.com");
        Assert.Equal(HostnameCoverage.OnlyInCommonName, r.Coverage);
        Assert.False(r.IsCovered);
    }

    [Fact]
    public void UnrelatedHost_IsNotCovered()
    {
        using var cert = SelfSigned("CN=www.example.com", dnsNames: ["www.example.com"]);
        var r = HostnameCheck.Check(cert, "elsewhere.test");
        Assert.Equal(HostnameCoverage.NotCovered, r.Coverage);
    }

    [Fact]
    public void IpAddressSan_IsCovered()
    {
        using var cert = SelfSigned("CN=device", ipAddresses: ["10.0.0.5"]);
        Assert.True(HostnameCheck.Check(cert, "10.0.0.5").IsCovered);
    }

    [Fact]
    public void EmptyHost_IsRejectedNotCrash()
    {
        using var cert = SelfSigned("CN=www.example.com", dnsNames: ["www.example.com"]);
        var r = HostnameCheck.Check(cert, "   ");
        Assert.False(r.IsCovered);
    }

    [Theory]
    [InlineData("not a host!!")]
    [InlineData("-.-")]
    [InlineData(":::")]
    [InlineData("тест.example.com")]
    public void MalformedHost_DoesNotThrow(string host)
    {
        using var cert = SelfSigned("CN=www.example.com", dnsNames: ["www.example.com"]);
        var ex = Record.Exception(() => HostnameCheck.Check(cert, host));
        Assert.Null(ex);
    }
}

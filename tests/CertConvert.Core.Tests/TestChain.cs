using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core.Tests;

/// <summary>Builds throwaway root → intermediate → leaf chains at test time.</summary>
internal static class TestChain
{
    internal sealed record Chain(
        X509Certificate2 Root,
        X509Certificate2 Intermediate,
        X509Certificate2 Leaf,
        RSA LeafKey) : IDisposable
    {
        public X509Certificate2[] All => [Root, Intermediate, Leaf];
        public X509Certificate2[] Shuffled => [Intermediate, Root, Leaf];
        public void Dispose()
        {
            Root.Dispose();
            Intermediate.Dispose();
            Leaf.Dispose();
            LeafKey.Dispose();
        }
    }

    internal static Chain Create(
        DateTimeOffset? leafNotBefore = null, DateTimeOffset? leafNotAfter = null)
    {
        var now = DateTimeOffset.UtcNow;

        // CA windows start well in the past so tests can issue backdated (expired) leaves.
        using var rootKey = RSA.Create(2048);
        var rootReq = NewCaRequest("CN=CertConvert Test Root CA", rootKey);
        var root = rootReq.CreateSelfSigned(now.AddYears(-5), now.AddYears(10));

        using var intKey = RSA.Create(2048);
        var intReq = NewCaRequest("CN=CertConvert Test Intermediate CA", intKey);
        intReq.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var intermediate = intReq.Create(
            root, now.AddYears(-5), now.AddYears(5), RandomNumberGenerator.GetBytes(8));
        using var intermediateWithKey = intermediate.CopyWithPrivateKey(intKey);

        var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            new X500DistinguishedName("CN=device.test.local"),
            leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafReq.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(leafReq.PublicKey, false));
        leafReq.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(intermediate, true, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("device.test.local");
        leafReq.CertificateExtensions.Add(san.Build());

        var leaf = leafReq.Create(
            intermediateWithKey,
            leafNotBefore ?? now.AddDays(-1),
            leafNotAfter ?? now.AddYears(1),
            RandomNumberGenerator.GetBytes(8));

        return new Chain(root, intermediate, leaf, leafKey);
    }

    private static CertificateRequest NewCaRequest(string dn, RSA key)
    {
        var req = new CertificateRequest(
            new X500DistinguishedName(dn), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req;
    }
}

using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

public enum KeyAlgorithmChoice
{
    Rsa2048,
    Rsa3072,
    Rsa4096,
    EcP256,
    EcP384,
    EcP521,
}

/// <summary>Subject and extension parameters for CSRs and self-signed certificates.</summary>
public sealed record CertSpec
{
    public required string CommonName { get; init; }
    public string? Organization { get; init; }
    public string? OrganizationalUnit { get; init; }
    /// <summary>Two-letter ISO country code.</summary>
    public string? Country { get; init; }
    public string? State { get; init; }
    public string? Locality { get; init; }
    public IReadOnlyList<string> DnsNames { get; init; } = [];
    public IReadOnlyList<string> IpAddresses { get; init; } = [];
    public int ValidityDays { get; init; } = 365;
    public bool IsCertificateAuthority { get; init; }
}

/// <summary>Key, CSR and self-signed certificate generation — the openssl req workflows.</summary>
public static class Generator
{
    public static PrivateKeyEntry CreateKey(KeyAlgorithmChoice choice) => choice switch
    {
        KeyAlgorithmChoice.Rsa2048 => KeyTools.Describe(RSA.Create(2048)),
        KeyAlgorithmChoice.Rsa3072 => KeyTools.Describe(RSA.Create(3072)),
        KeyAlgorithmChoice.Rsa4096 => KeyTools.Describe(RSA.Create(4096)),
        KeyAlgorithmChoice.EcP256 => KeyTools.Describe(ECDsa.Create(ECCurve.NamedCurves.nistP256)),
        KeyAlgorithmChoice.EcP384 => KeyTools.Describe(ECDsa.Create(ECCurve.NamedCurves.nistP384)),
        KeyAlgorithmChoice.EcP521 => KeyTools.Describe(ECDsa.Create(ECCurve.NamedCurves.nistP521)),
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    public static string CreateCsrPem(AsymmetricAlgorithm key, CertSpec spec)
    {
        var request = BuildRequest(key, spec);
        return PemUtil.Encode("CERTIFICATE REQUEST", request.CreateSigningRequest());
    }

    public static X509Certificate2 CreateSelfSigned(AsymmetricAlgorithm key, CertSpec spec)
    {
        if (spec.ValidityDays < 1)
            throw new CertConvertException("Validity must be at least one day.");
        var request = BuildRequest(key, spec);
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(spec.ValidityDays));
    }

    private static CertificateRequest BuildRequest(AsymmetricAlgorithm key, CertSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.CommonName))
            throw new CertConvertException("A common name (CN) is required.");

        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(spec.CommonName);
        if (!string.IsNullOrWhiteSpace(spec.Organization)) dn.AddOrganizationName(spec.Organization);
        if (!string.IsNullOrWhiteSpace(spec.OrganizationalUnit)) dn.AddOrganizationalUnitName(spec.OrganizationalUnit);
        if (!string.IsNullOrWhiteSpace(spec.State)) dn.AddStateOrProvinceName(spec.State);
        if (!string.IsNullOrWhiteSpace(spec.Locality)) dn.AddLocalityName(spec.Locality);
        if (!string.IsNullOrWhiteSpace(spec.Country))
        {
            string country = spec.Country.Trim().ToUpperInvariant();
            if (country.Length != 2 || !country.All(char.IsAsciiLetter))
                throw new CertConvertException("Country must be a two-letter ISO code, e.g. GB.");
            dn.AddCountryOrRegion(country); // ISO 3166-1 alpha-2 is canonical uppercase
        }

        CertificateRequest request = key switch
        {
            RSA rsa => new CertificateRequest(
                dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            // Match the signature hash to the curve strength (SHA-384/512 for
            // P-384/P-521); RSA stays SHA-256, which is standard practice.
            ECDsa ec => new CertificateRequest(dn.Build(), ec, EcHash(ec)),
            _ => throw new CertConvertException(
                $"Unsupported key algorithm: {key.GetType().Name}."),
        };

        if (spec.IsCertificateAuthority)
        {
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        }
        else
        {
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], // server + client auth
                critical: false));
        }

        if (spec.DnsNames.Count > 0 || spec.IpAddresses.Count > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var dns in spec.DnsNames.Where(d => !string.IsNullOrWhiteSpace(d)))
                san.AddDnsName(dns.Trim());
            foreach (var ip in spec.IpAddresses.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                if (!IPAddress.TryParse(ip.Trim(), out var parsed))
                    throw new CertConvertException($"\"{ip}\" is not a valid IP address.");
                san.AddIpAddress(parsed);
            }
            request.CertificateExtensions.Add(san.Build());
        }

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static HashAlgorithmName EcHash(ECDsa ec) => ec.KeySize switch
    {
        >= 512 => HashAlgorithmName.SHA512,   // P-521
        >= 384 => HashAlgorithmName.SHA384,   // P-384
        _ => HashAlgorithmName.SHA256,        // P-256
    };
}

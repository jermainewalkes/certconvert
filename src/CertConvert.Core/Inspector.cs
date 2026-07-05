using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>Decodes certificates into display models — the openssl x509 -text equivalent.</summary>
public static class Inspector
{
    public static CertificateInfo Inspect(X509Certificate2 cert)
    {
        var sans = new List<string>();
        var keyUsages = new List<string>();
        var ekus = new List<string>();
        bool hasBasicConstraints = false, isCa = false;
        int? pathLen = null;
        string? ski = null, aki = null;

        // Dispatch on OID: cert.Extensions does not reliably materialise the typed
        // extension classes (notably SAN and AKI), so rebuild them from raw data.
        foreach (var ext in cert.Extensions)
        {
            switch (ext.Oid?.Value)
            {
                case "2.5.29.17": // subjectAltName
                {
                    var san = new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
                    foreach (var dns in san.EnumerateDnsNames())
                        sans.Add($"DNS:{dns}");
                    foreach (var ip in san.EnumerateIPAddresses())
                        sans.Add($"IP:{ip}");
                    break;
                }
                case "2.5.29.15": // keyUsage
                {
                    var ku = new X509KeyUsageExtension();
                    ku.CopyFrom(ext);
                    foreach (X509KeyUsageFlags flag in Enum.GetValues<X509KeyUsageFlags>())
                        if (flag != X509KeyUsageFlags.None && ku.KeyUsages.HasFlag(flag))
                            keyUsages.Add(flag.ToString());
                    break;
                }
                case "2.5.29.37": // extendedKeyUsage
                {
                    var eku = new X509EnhancedKeyUsageExtension();
                    eku.CopyFrom(ext);
                    foreach (var oid in eku.EnhancedKeyUsages)
                        ekus.Add(oid.FriendlyName ?? oid.Value ?? "unknown");
                    break;
                }
                case "2.5.29.19": // basicConstraints
                {
                    var bc = new X509BasicConstraintsExtension();
                    bc.CopyFrom(ext);
                    hasBasicConstraints = true;
                    isCa = bc.CertificateAuthority;
                    if (bc.HasPathLengthConstraint)
                        pathLen = bc.PathLengthConstraint;
                    break;
                }
                case "2.5.29.14": // subjectKeyIdentifier
                {
                    var skiExt = new X509SubjectKeyIdentifierExtension();
                    skiExt.CopyFrom(ext);
                    ski = FormatHex(Convert.FromHexString(skiExt.SubjectKeyIdentifier ?? ""));
                    break;
                }
                case "2.5.29.35": // authorityKeyIdentifier
                {
                    var akiExt = new X509AuthorityKeyIdentifierExtension(ext.RawData, ext.Critical);
                    if (akiExt.KeyIdentifier is { } kid)
                        aki = FormatHex(kid.Span);
                    break;
                }
            }
        }

        return new CertificateInfo
        {
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            SerialNumber = cert.SerialNumber,
            NotBefore = cert.NotBefore.ToUniversalTime(),
            NotAfter = cert.NotAfter.ToUniversalTime(),
            Version = cert.Version,
            KeyAlgorithm = DescribePublicKey(cert),
            SignatureAlgorithm = cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "unknown",
            Sha1Fingerprint = FormatHex(cert.GetCertHash(HashAlgorithmName.SHA1)),
            Sha256Fingerprint = FormatHex(cert.GetCertHash(HashAlgorithmName.SHA256)),
            IsSelfSigned = cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData),
            HasBasicConstraints = hasBasicConstraints,
            IsCertificateAuthority = isCa,
            PathLengthConstraint = pathLen,
            SubjectAlternativeNames = sans,
            KeyUsages = keyUsages,
            EnhancedKeyUsages = ekus,
            SubjectKeyIdentifier = ski,
            AuthorityKeyIdentifier = aki,
        };
    }

    public static string DescribePublicKey(X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is not null)
            return $"RSA {rsa.KeySize}-bit";
        using var ec = cert.GetECDsaPublicKey();
        if (ec is not null)
        {
            string? curve = null;
            try { curve = ec.ExportParameters(false).Curve.Oid?.FriendlyName; }
            catch (CryptographicException) { }
            return curve is null ? $"ECDSA {ec.KeySize}-bit" : $"ECDSA {curve}";
        }
        return cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "unknown";
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        var hex = Convert.ToHexString(bytes);
        var parts = new string[hex.Length / 2];
        for (int i = 0; i < parts.Length; i++)
            parts[i] = hex.Substring(i * 2, 2);
        return string.Join(":", parts);
    }

    /// <summary>Renders one certificate as readable multi-line text (CLI output).</summary>
    public static string ToText(CertificateInfo info)
    {
        var w = new System.Text.StringBuilder();
        w.AppendLine($"Subject:       {info.Subject}");
        w.AppendLine($"Issuer:        {info.Issuer}{(info.IsSelfSigned ? "  (self-signed)" : "")}");
        w.AppendLine($"Serial:        {info.SerialNumber}");
        w.AppendLine($"Valid from:    {info.NotBefore:yyyy-MM-dd HH:mm:ss} UTC");
        string expiry = info.IsExpired ? "  ** EXPIRED **"
            : info.IsNotYetValid ? "  ** NOT YET VALID **" : "";
        w.AppendLine($"Valid until:   {info.NotAfter:yyyy-MM-dd HH:mm:ss} UTC{expiry}");
        w.AppendLine($"Public key:    {info.KeyAlgorithm}");
        w.AppendLine($"Signature:     {info.SignatureAlgorithm}");
        if (info.HasBasicConstraints)
        {
            string ca = info.IsCertificateAuthority ? "CA" : "end entity";
            if (info.PathLengthConstraint is { } n) ca += $", path length {n}";
            w.AppendLine($"Constraints:   {ca}");
        }
        if (info.SubjectAlternativeNames.Count > 0)
            w.AppendLine($"SANs:          {string.Join(", ", info.SubjectAlternativeNames)}");
        if (info.KeyUsages.Count > 0)
            w.AppendLine($"Key usage:     {string.Join(", ", info.KeyUsages)}");
        if (info.EnhancedKeyUsages.Count > 0)
            w.AppendLine($"Extended use:  {string.Join(", ", info.EnhancedKeyUsages)}");
        if (info.SubjectKeyIdentifier is not null)
            w.AppendLine($"Subject KI:    {info.SubjectKeyIdentifier}");
        if (info.AuthorityKeyIdentifier is not null)
            w.AppendLine($"Authority KI:  {info.AuthorityKeyIdentifier}");
        w.AppendLine($"SHA-256:       {info.Sha256Fingerprint}");
        w.AppendLine($"SHA-1:         {info.Sha1Fingerprint}");
        return w.ToString();
    }
}

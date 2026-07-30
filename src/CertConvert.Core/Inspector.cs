using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>Typed extension data rebuilt from raw OIDs — shared by the certificate
/// and certificate-request inspectors.</summary>
internal sealed class ExtensionData
{
    public List<string> Sans { get; } = new();
    public List<string> KeyUsages { get; } = new();
    public List<string> Ekus { get; } = new();
    public bool HasBasicConstraints { get; set; }
    public bool IsCa { get; set; }
    public int? PathLen { get; set; }
    public string? Ski { get; set; }
    public string? Aki { get; set; }
    /// <summary>Raw key-usage flags, for locale-proof analysis (never name-match).</summary>
    public X509KeyUsageFlags KeyUsageFlags { get; set; }
    /// <summary>EKU purpose OIDs, for locale-proof analysis.</summary>
    public List<string> EkuOids { get; } = new();
    /// <summary>OIDs of extensions that would not parse (request extensions are attacker-controlled).</summary>
    public List<string> Unreadable { get; } = new();
}

/// <summary>Decodes certificates into display models — the openssl x509 -text equivalent.</summary>
public static class Inspector
{
    public static CertificateInfo Inspect(X509Certificate2 cert)
    {
        var ext = ParseExtensions(cert.Extensions);
        var (keyDisplay, keyAlgo, keyBits) = DescribeKey(cert.PublicKey);

        return new CertificateInfo
        {
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            SerialNumber = cert.SerialNumber,
            NotBefore = cert.NotBefore.ToUniversalTime(),
            NotAfter = cert.NotAfter.ToUniversalTime(),
            Version = cert.Version,
            KeyAlgorithm = keyDisplay,
            PublicKeyAlgorithm = keyAlgo,
            PublicKeyBits = keyBits,
            SignatureAlgorithm = cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "unknown",
            SignatureAlgorithmOid = cert.SignatureAlgorithm.Value ?? "",
            Sha1Fingerprint = FormatHex(cert.GetCertHash(HashAlgorithmName.SHA1)),
            Sha256Fingerprint = FormatHex(cert.GetCertHash(HashAlgorithmName.SHA256)),
            IsSelfSigned = cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData),
            IsSelfSignatureValid = SelfSignatureVerifies(cert),
            HasBasicConstraints = ext.HasBasicConstraints,
            IsCertificateAuthority = ext.IsCa,
            PathLengthConstraint = ext.PathLen,
            SubjectAlternativeNames = ext.Sans,
            KeyUsages = ext.KeyUsages,
            EnhancedKeyUsages = ext.Ekus,
            KeyUsageFlags = ext.KeyUsageFlags,
            EnhancedKeyUsageOids = ext.EkuOids,
            SubjectKeyIdentifier = ext.Ski,
            AuthorityKeyIdentifier = ext.Aki,
        };
    }

    /// <summary>
    /// Rebuilds typed extension data from raw OIDs. <c>X509Certificate2.Extensions</c>
    /// does not reliably materialise the typed extension classes (notably SAN and
    /// AKI), so dispatch on OID and parse the raw data ourselves. Shared by the
    /// certificate inspector and the CSR inspector. A single extension that fails to
    /// parse is recorded in <see cref="ExtensionData.Unreadable"/> rather than
    /// aborting the whole decode — request extensions in particular are
    /// attacker-controlled and must never crash the reader.
    /// </summary>
    internal static ExtensionData ParseExtensions(IEnumerable<X509Extension> extensions)
    {
        var d = new ExtensionData();
        foreach (var ext in extensions)
        {
            try
            {
                switch (ext.Oid?.Value)
                {
                    case "2.5.29.17": // subjectAltName
                    {
                        var san = new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
                        foreach (var dns in san.EnumerateDnsNames())
                            d.Sans.Add($"DNS:{dns}");
                        foreach (var ip in san.EnumerateIPAddresses())
                            d.Sans.Add($"IP:{ip}");
                        break;
                    }
                    case "2.5.29.15": // keyUsage
                    {
                        var ku = new X509KeyUsageExtension();
                        ku.CopyFrom(ext);
                        d.KeyUsageFlags = ku.KeyUsages;
                        foreach (X509KeyUsageFlags flag in Enum.GetValues<X509KeyUsageFlags>())
                            if (flag != X509KeyUsageFlags.None && ku.KeyUsages.HasFlag(flag))
                                d.KeyUsages.Add(flag.ToString());
                        break;
                    }
                    case "2.5.29.37": // extendedKeyUsage
                    {
                        var eku = new X509EnhancedKeyUsageExtension();
                        eku.CopyFrom(ext);
                        foreach (var oid in eku.EnhancedKeyUsages)
                        {
                            d.Ekus.Add(oid.FriendlyName ?? oid.Value ?? "unknown");
                            if (oid.Value is { } v) d.EkuOids.Add(v);
                        }
                        break;
                    }
                    case "2.5.29.19": // basicConstraints
                    {
                        var bc = new X509BasicConstraintsExtension();
                        bc.CopyFrom(ext);
                        d.HasBasicConstraints = true;
                        d.IsCa = bc.CertificateAuthority;
                        if (bc.HasPathLengthConstraint)
                            d.PathLen = bc.PathLengthConstraint;
                        break;
                    }
                    case "2.5.29.14": // subjectKeyIdentifier
                    {
                        var skiExt = new X509SubjectKeyIdentifierExtension();
                        skiExt.CopyFrom(ext);
                        // SubjectKeyIdentifier is already a hex string; colon-group it
                        // directly rather than decode-to-bytes then re-encode.
                        d.Ski = FormatHexString(skiExt.SubjectKeyIdentifier ?? "");
                        break;
                    }
                    case "2.5.29.35": // authorityKeyIdentifier
                    {
                        var akiExt = new X509AuthorityKeyIdentifierExtension(ext.RawData, ext.Critical);
                        if (akiExt.KeyIdentifier is { } kid)
                            d.Aki = FormatHex(kid.Span);
                        break;
                    }
                }
            }
            catch (Exception e) when (
                e is CryptographicException
                or FormatException
                or System.Formats.Asn1.AsnContentException
                or ArgumentException
                or OverflowException)
            {
                // Request extensions are attacker-controlled: any parse failure is
                // recorded as unreadable rather than allowed to crash the reader.
                d.Unreadable.Add(ext.Oid?.Value ?? "unknown");
            }
        }
        return d;
    }

    // Signature-algorithm OID → (hash, is-ECDSA), for verifying a self-signature.
    private static readonly Dictionary<string, (HashAlgorithmName Hash, bool IsEc)> SignatureAlgorithms = new()
    {
        ["1.2.840.113549.1.1.4"]  = (HashAlgorithmName.MD5, false),
        ["1.2.840.113549.1.1.5"]  = (HashAlgorithmName.SHA1, false),
        ["1.3.14.3.2.29"]         = (HashAlgorithmName.SHA1, false),
        ["1.2.840.113549.1.1.11"] = (HashAlgorithmName.SHA256, false),
        ["1.2.840.113549.1.1.12"] = (HashAlgorithmName.SHA384, false),
        ["1.2.840.113549.1.1.13"] = (HashAlgorithmName.SHA512, false),
        ["1.2.840.10045.4.1"]     = (HashAlgorithmName.SHA1, true),
        ["1.2.840.10045.4.3.2"]   = (HashAlgorithmName.SHA256, true),
        ["1.2.840.10045.4.3.3"]   = (HashAlgorithmName.SHA384, true),
        ["1.2.840.10045.4.3.4"]   = (HashAlgorithmName.SHA512, true),
    };

    /// <summary>
    /// Whether the certificate's signature actually verifies against its own public
    /// key — i.e. it is genuinely self-signed, not merely self-<em>issued</em>
    /// (subject DN == issuer DN). Returns false whenever this can't be positively
    /// proven, so callers that relax a check for "a self-signed root" never do so on
    /// an unverified certificate (e.g. a cross-signed cert with matching DNs, or a
    /// PSS/MD2 signature we don't verify here). Only meaningful for self-issued
    /// certificates.
    /// </summary>
    internal static bool SelfSignatureVerifies(X509Certificate2 cert)
    {
        if (!cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData))
            return false;
        if (!SignatureAlgorithms.TryGetValue(cert.SignatureAlgorithm.Value ?? "", out var alg))
            return false;
        try
        {
            // Certificate ::= SEQUENCE { tbsCertificate SEQUENCE, signatureAlgorithm,
            //                            signatureValue BIT STRING }
            var top = new AsnReader(cert.RawData, AsnEncodingRules.DER).ReadSequence();
            byte[] tbs = top.ReadEncodedValue().ToArray();  // exactly the bytes that were signed
            top.ReadSequence();                              // signatureAlgorithm (skip)
            byte[] sig = top.ReadBitString(out _);           // signatureValue

            if (!alg.IsEc)
            {
                using var rsa = cert.GetRSAPublicKey();
                return rsa is not null && rsa.VerifyData(tbs, sig, alg.Hash, RSASignaturePadding.Pkcs1);
            }
            using var ec = cert.GetECDsaPublicKey();
            return ec is not null && ec.VerifyData(tbs, sig, alg.Hash, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException or ArgumentException)
        {
            return false;
        }
    }

    public static string DescribePublicKey(X509Certificate2 cert) => DescribeKey(cert.PublicKey).Display;

    /// <summary>Describes any SubjectPublicKeyInfo: a display string plus the
    /// structured algorithm/size used by the health check (never name-matched).</summary>
    internal static (string Display, string Algorithm, int Bits) DescribeKey(PublicKey pk)
    {
        using var rsa = pk.GetRSAPublicKey();
        if (rsa is not null)
            return ($"RSA {rsa.KeySize}-bit", "RSA", rsa.KeySize);
        using var ec = pk.GetECDsaPublicKey();
        if (ec is not null)
        {
            string? curve = null;
            try { curve = ec.ExportParameters(false).Curve.Oid?.FriendlyName; }
            catch (CryptographicException) { }
            string display = curve is null ? $"ECDSA {ec.KeySize}-bit" : $"ECDSA {curve}";
            return (display, "ECDSA", ec.KeySize);
        }
        return (pk.Oid.FriendlyName ?? pk.Oid.Value ?? "unknown", pk.Oid.Value ?? "unknown", 0);
    }

    /// <summary>The common name of a distinguished name, or a sensible fallback.
    /// Parses the DN properly (via <see cref="X500DistinguishedName"/>) so an
    /// escaped or quoted comma inside a value doesn't split the name.</summary>
    internal static string CommonNameOf(string subject)
    {
        if (subject.Length == 0) return "(no subject)";
        try
        {
            foreach (var rdn in new X500DistinguishedName(subject).EnumerateRelativeDistinguishedNames())
                if (rdn.GetSingleElementType().Value == "2.5.4.3") // commonName
                    return rdn.GetSingleElementValue() ?? subject;
        }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException)
        {
            // Unparseable, or a multi-valued RDN — fall back to the raw subject.
        }
        return subject;
    }

    internal static string FormatHex(ReadOnlySpan<byte> bytes) => FormatHexString(Convert.ToHexString(bytes));

    /// <summary>Groups an unseparated hex string into colon-delimited byte pairs.</summary>
    internal static string FormatHexString(string hex)
    {
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

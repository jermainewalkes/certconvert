using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>How serious a health-check finding is.</summary>
public enum Severity
{
    /// <summary>The certificate will not work, or is unsafe.</summary>
    Critical,
    /// <summary>Works today, but wrong for its apparent purpose or nearly broken.</summary>
    Warning,
    /// <summary>Worth knowing; not a problem in itself.</summary>
    Notice,
}

/// <summary>One plain-English health-check finding: what is wrong and what to do.</summary>
public sealed record Finding(Severity Severity, string Id, string Title, string Explanation);

/// <summary>
/// A small, deliberately conservative certificate linter for people who would
/// rather not memorise the rules. Findings are role-aware: a 20-year private root
/// is correct practice and must not be nagged the way a public TLS certificate
/// would be — that restraint is the difference between a linter people trust and
/// one they mute. All algorithm and key-usage tests match on OID/flags, never on
/// localised friendly names.
/// </summary>
public static class HealthCheck
{
    private const string ServerAuthEku = "1.3.6.1.5.5.7.3.1";

    // Signature algorithm OIDs that rely on a broken hash. Matched by OID so the
    // result never depends on the machine's locale.
    private static readonly HashSet<string> WeakSignatureOids = new()
    {
        "1.2.840.113549.1.1.2", // md2WithRSAEncryption
        "1.2.840.113549.1.1.4", // md5WithRSAEncryption
        "1.2.840.113549.1.1.5", // sha1WithRSAEncryption
        "1.3.14.3.2.29",        // sha1WithRSA (older)
        "1.2.840.10040.4.3",    // dsa-with-sha1
        "1.2.840.10045.4.1",    // ecdsa-with-SHA1
    };

    private enum Role { Ca, TlsServer, Other }

    /// <summary>Analyses a certificate. <paramref name="now"/> is injectable so
    /// expiry findings are deterministic in tests.</summary>
    public static IReadOnlyList<Finding> Analyse(CertificateInfo cert, DateTimeOffset? now = null)
    {
        var when = now ?? DateTimeOffset.UtcNow;
        var findings = new List<Finding>();
        var role = RoleOf(cert);

        // --- Critical --------------------------------------------------------
        if (when > cert.NotAfter)
            findings.Add(new(Severity.Critical, "expired", "Expired",
                $"It stopped being valid on {cert.NotAfter:yyyy-MM-dd}. Reissue or renew it — nothing will accept it now."));
        else if (when < cert.NotBefore)
            findings.Add(new(Severity.Critical, "not-yet-valid", "Not valid yet",
                $"It does not become valid until {cert.NotBefore:yyyy-MM-dd}. Check the clock on the machine that issued it."));

        if (cert.PublicKeyAlgorithm == "RSA" && cert.PublicKeyBits > 0 && cert.PublicKeyBits < 2048)
            findings.Add(new(Severity.Critical, "weak-rsa-key", "RSA key is too small",
                $"A {cert.PublicKeyBits}-bit RSA key is below the 2048-bit minimum browsers and CAs require. Generate a new 2048-bit (or larger) key."));

        if (cert.PublicKeyAlgorithm == "ECDSA" && cert.PublicKeyBits > 0 && cert.PublicKeyBits < 224)
            findings.Add(new(Severity.Critical, "weak-ec-key", "EC key is too small",
                $"A {cert.PublicKeyBits}-bit elliptic-curve key is below today's minimum. Use P-256 (or a larger curve)."));

        if (WeakSignatureOids.Contains(cert.SignatureAlgorithmOid))
        {
            // Only a genuinely self-signed *root* (a trust anchor whose signature
            // verifies against its own key) earns the downgrade: its own signature
            // is never checked during path validation. A self-signed leaf — or a
            // merely self-issued cert whose signature does not verify — is still
            // validated by whatever relies on it, so its weak hash stays Critical.
            if (cert.IsSelfSigned && cert.IsCertificateAuthority && cert.IsSelfSignatureValid)
                findings.Add(new(Severity.Notice, "weak-signature", "Signed with a broken hash (SHA-1, MD5 or MD2)",
                    "This is a self-signed root, so its own signature is never checked — but reissue it with SHA-256 before anything relies on it."));
            else
                findings.Add(new(Severity.Critical, "weak-signature", "Signed with a broken hash (SHA-1, MD5 or MD2)",
                    "SHA-1, MD5 and MD2 are broken and rejected by modern clients. Have it reissued with a SHA-256 signature."));
        }

        if (role == Role.TlsServer && cert.SubjectAlternativeNames.Count == 0)
            findings.Add(new(Severity.Critical, "no-san", "No subject alternative name",
                "Browsers stopped reading the common name in 2017 — host names must be listed in the SAN extension. As it stands, this certificate will not be accepted for any host name."));

        if (role == Role.Ca && cert.KeyUsageFlags != X509KeyUsageFlags.None
            && !cert.KeyUsageFlags.HasFlag(X509KeyUsageFlags.KeyCertSign))
            findings.Add(new(Severity.Critical, "ca-without-keycertsign", "CA cannot sign certificates",
                "This is marked as a certificate authority, but its key usage leaves out keyCertSign, so it cannot actually issue certificates. Reissue it with keyCertSign set."));

        // A certificate authority that also carries the TLS server-authentication
        // purpose is a dangerous mix-up: a CA must not double as a server leaf.
        if (cert.IsCertificateAuthority && cert.EnhancedKeyUsageOids.Contains(ServerAuthEku))
            findings.Add(new(Severity.Critical, "ee-asserting-ca", "Both a CA and a TLS server certificate",
                "This is marked as a certificate authority yet also carries TLS server-authentication usage. A CA should never be usable as a server certificate — issue a separate leaf certificate for the server and keep the CA for signing."));

        // --- Warning ---------------------------------------------------------
        if (when <= cert.NotAfter && (cert.NotAfter - when).TotalDays <= 30)
            findings.Add(new(Severity.Warning, "expiring-soon", "Expires soon",
                $"It expires on {cert.NotAfter:yyyy-MM-dd}. Renew it before then to avoid an outage."));

        if (role == Role.TlsServer && (cert.NotAfter - cert.NotBefore).TotalDays > 398)
            findings.Add(new(Severity.Warning, "long-validity", "Valid for longer than browsers allow",
                "Since 2020 a public TLS certificate may last at most 398 days. Fine for a private, internal certificate; a public CA will not issue one this long."));

        if (role == Role.Ca && cert.SubjectKeyIdentifier is null)
            findings.Add(new(Severity.Warning, "ca-missing-ski", "CA has no subject key identifier",
                "Certificates this CA issues reference it by a subject key identifier it does not have, which trips up some chain builders. Reissue it with one."));

        // --- Notice ----------------------------------------------------------
        // Only a *verified* self-signature earns "it vouches for itself" — a merely
        // self-issued cert (matching DNs, signed by another key) does not.
        if (cert.IsSelfSigned && cert.IsSelfSignatureValid)
            findings.Add(new(Severity.Notice, "self-signed", "Self-signed",
                "It vouches for itself, so nothing trusts it until you install it as a root. Normal for a test rig or an internal service."));

        return findings;
    }

    /// <summary>Analyses a certificate request before it is submitted to a CA.</summary>
    public static IReadOnlyList<Finding> Analyse(CsrInfo csr)
    {
        var findings = new List<Finding>();

        if (csr.SignatureState == CsrSignatureState.Invalid)
            findings.Add(new(Severity.Critical, "signature-invalid", "Signature does not verify",
                "The request is not signed by the private key it names, so a CA will reject it. Regenerate it from the matching key."));

        if (csr.PublicKeyAlgorithm == "RSA" && csr.PublicKeyBits > 0 && csr.PublicKeyBits < 2048)
            findings.Add(new(Severity.Critical, "weak-rsa-key", "RSA key is too small",
                $"A {csr.PublicKeyBits}-bit RSA key is below the 2048-bit minimum a CA will accept. Generate a new 2048-bit (or larger) key and request again."));

        string cn = Inspector.CommonNameOf(csr.Subject);
        bool looksLikeServer = csr.EnhancedKeyUsageOids.Contains(ServerAuthEku)
            || (csr.EnhancedKeyUsageOids.Count == 0 && LooksLikeHostname(cn));
        if (looksLikeServer && csr.SubjectAlternativeNames.Count == 0)
            findings.Add(new(Severity.Warning, "no-san", "No subject alternative name requested",
                "The request names a host only in its common name. Browsers ignore the common name, so add the host to the subject alternative name before submitting it."));

        if (WeakSignatureOids.Contains(csr.SignatureAlgorithmOid))
            findings.Add(new(Severity.Notice, "weak-signature", "Signed with a broken hash (SHA-1, MD5 or MD2)",
                "The request itself uses a weak hash. The issued certificate's signature is chosen by the CA, so this is cosmetic — but regenerate it with SHA-256 to be tidy."));

        if (csr.UnreadableExtensions.Count > 0)
            findings.Add(new(Severity.Notice, "unreadable-extensions", "Some requested extensions could not be read",
                $"These requested extensions did not parse and are shown as-is: {string.Join(", ", csr.UnreadableExtensions)}."));

        return findings;
    }

    /// <summary>Renders findings as readable multi-line text (CLI output).</summary>
    public static string ToText(IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
            return "Health check:  no problems found." + Environment.NewLine;

        var w = new System.Text.StringBuilder();
        w.AppendLine("Health check:");
        foreach (var f in findings)
            w.AppendLine($"  [{f.Severity.ToString().ToUpperInvariant()}] {f.Title} — {f.Explanation}");
        return w.ToString();
    }

    private static Role RoleOf(CertificateInfo cert)
    {
        if (cert.IsCertificateAuthority)
            return Role.Ca;

        bool hasServerAuth = cert.EnhancedKeyUsageOids.Contains(ServerAuthEku);
        if (hasServerAuth)
            return Role.TlsServer;

        // A certificate with no EKU restriction that either carries a DNS/IP SAN or
        // has a host-name-shaped common name is almost always a TLS server cert. The
        // SAN branch catches SAN-only certs (excessive-validity checks); the CN
        // branch catches the CN-only case the no-SAN check exists to flag — and it
        // must not require a SAN, the trap the earlier heuristic fell into. Only
        // DNS/IP SANs are counted (email/other SANs never reach this list), so an
        // S/MIME certificate is not misread as a server.
        if (cert.EnhancedKeyUsageOids.Count == 0
            && (cert.SubjectAlternativeNames.Count > 0
                || LooksLikeHostname(Inspector.CommonNameOf(cert.Subject))))
            return Role.TlsServer;

        return Role.Other;
    }

    /// <summary>A rough "is this common name a host name?" test — enough to decide
    /// whether a no-EKU certificate should be treated as a TLS server. Rejects email
    /// addresses (S/MIME) and IP-like strings, and requires an alphabetic final label.</summary>
    private static bool LooksLikeHostname(string cn)
    {
        if (cn.Length == 0 || cn.Contains(' ') || cn.Contains('=') || cn.Contains('@') || !cn.Contains('.'))
            return false;
        var labels = cn.Split('.');
        if (labels.Any(l => l.Length == 0))
            return false;
        string tld = labels[^1];
        return tld.Length >= 2 && tld.All(char.IsAsciiLetter);
    }
}

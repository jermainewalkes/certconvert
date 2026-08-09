using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>The kind of end-entity certificate to issue. Subordinate-CA issuance is
/// deliberately not offered here (deferred — it needs path-length propagation and
/// name-constraint handling).</summary>
public enum IssuedRole
{
    /// <summary>TLS server: serverAuth EKU; RSA gets keyEncipherment, ECDSA does not.</summary>
    TlsServer,
    /// <summary>TLS client: clientAuth EKU; digitalSignature only.</summary>
    TlsClient,
}

/// <summary>What the operator authorises for one issuance. Everything here is the
/// issuer's decision — none of it is taken from the request.</summary>
public sealed record IssuanceOptions
{
    public int ValidityDays { get; init; } = 365;
    public IssuedRole Role { get; init; } = IssuedRole.TlsServer;
    public IReadOnlyList<string> DnsNames { get; init; } = [];
    public IReadOnlyList<string> IpAddresses { get; init; } = [];
    public IReadOnlyList<string> EmailNames { get; init; } = [];
    public IReadOnlyList<string> Uris { get; init; } = [];
    /// <summary>The ONLY thing the "--force" / unsafe override bypasses: signing with a
    /// certificate that isn't marked as a CA. Never bypasses expiry or key-mismatch.</summary>
    public bool AllowNonCaIssuer { get; init; }
}

/// <summary>
/// Signs a PKCS #10 request with a CA certificate + key, producing an END-ENTITY
/// certificate chained to that CA. This is CertConvert's mini-CA.
/// </summary>
/// <remarks>
/// SECURITY CONTRACT — a request's extensions are attacker-controlled and are NEVER
/// copied into the issued certificate. The signer reads only the request's subject and
/// public key (after verifying proof-of-possession) and builds a fresh certificate whose
/// extensions are entirely issuer-chosen. SANs come from the operator-confirmed
/// <see cref="IssuanceOptions"/> string lists (re-encoded here), not from the request's
/// bytes. The issued cert is always <c>CA:FALSE</c>.
/// </remarks>
public static class CaSigner
{
    private const int MaxRequestBytes = 1 * 1024 * 1024;
    private const string ServerAuthEku = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthEku = "1.3.6.1.5.5.7.3.2";
    private const string RsaKeyOid = "1.2.840.113549.1.1.1";

    public static X509Certificate2 Sign(
        X509Certificate2 caCert, AsymmetricAlgorithm caKey, byte[] csrDer, IssuanceOptions opts)
    {
        if (opts.ValidityDays is < 1 or > 366 * 100)
            throw new CertConvertException("Validity must be between 1 day and about 100 years.");
        if (opts.Role is not (IssuedRole.TlsServer or IssuedRole.TlsClient))
            throw new CertConvertException("Unknown certificate role.");
        // A TLS server certificate is worthless without a SAN — browsers ignore the CN.
        if (opts.Role == IssuedRole.TlsServer
            && !opts.DnsNames.Any(v => v.Trim().Length > 0)
            && !opts.IpAddresses.Any(v => v.Trim().Length > 0))
            throw new CertConvertException(
                "A TLS server certificate needs at least one DNS name or IP address "
                + "(browsers ignore the common name). Provide a DNS or IP SAN.");
        if (csrDer.Length is 0 or > MaxRequestBytes)
            throw new UnrecognisedContentException("The certificate request is empty or far too large.");

        // --- 1. Validate the CA (hard preconditions) --------------------------------
        if (caKey is not (RSA or ECDsa))
            throw new CertConvertException("The CA key must be an RSA or ECDSA key.");
        if (!KeyTools.Matches(caCert, caKey))
            throw new CertConvertException("The CA private key does not match the CA certificate.");

        var now = DateTimeOffset.UtcNow;
        if (now.UtcDateTime < caCert.NotBefore.ToUniversalTime()
            || now.UtcDateTime > caCert.NotAfter.ToUniversalTime())
            throw new CertConvertException(
                "The CA certificate is not currently valid (expired or not yet valid) — it cannot sign.");

        var caInfo = Inspector.Inspect(caCert);
        bool caUsable = caInfo.IsCertificateAuthority
            && (caInfo.KeyUsageFlags == X509KeyUsageFlags.None
                || caInfo.KeyUsageFlags.HasFlag(X509KeyUsageFlags.KeyCertSign));
        if (!caUsable && !opts.AllowNonCaIssuer)
            throw new CertConvertException(
                "The signing certificate is not marked as a certificate authority "
                + "(basic constraints CA:TRUE, plus keyCertSign when a key-usage extension is present). "
                + "Certificates it issues would not chain-validate. Enable the override to sign with it anyway.");

        // --- 2. Load the request, VALIDATING (enforce proof-of-possession) ----------
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(csrDer, CsrInspector.SignerHashAlgorithm(csrDer));
        }
        catch (Exception e) when (
            e is CryptographicException or System.Formats.Asn1.AsnContentException or ArgumentException)
        {
            throw new CertConvertException(
                "The certificate request could not be verified — its signature does not match its key "
                + "(proof of possession failed), or it is malformed. It will not be signed.", e);
        }

        // --- 3. Build a FRESH request: only the subject + public key are reused ------
        var issuedHash = caKey switch
        {
            ECDsa ec => Generator.EcHash(ec),
            _ => HashAlgorithmName.SHA256,        // RSA CA
        };
        var request = new CertificateRequest(csr.SubjectName, csr.PublicKey, issuedHash);

        // Always an end-entity — a requested CA:TRUE is ignored.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        // keyUsage by role AND by the issued key's algorithm (RSA vs ECDSA).
        bool issuedIsRsa = csr.PublicKey.Oid?.Value == RsaKeyOid;
        var keyUsage = opts.Role == IssuedRole.TlsServer
            ? (issuedIsRsa
                ? X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment
                : X509KeyUsageFlags.DigitalSignature)          // ECDSA: no keyEncipherment/keyAgreement
            : X509KeyUsageFlags.DigitalSignature;              // client
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, true));

        string ekuOid = opts.Role == IssuedRole.TlsServer ? ServerAuthEku : ClientAuthEku;
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ekuOid) }, false));

        // SANs re-encoded from the operator-confirmed lists — never the request's bytes.
        AddSubjectAltNames(request, csr.SubjectName, opts);

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false));

        // --- 4. Validity: clamp notAfter to the CA; serial positive + non-zero ------
        var caNotBefore = new DateTimeOffset(caCert.NotBefore.ToUniversalTime());
        var notBefore = now.AddMinutes(-5);
        if (notBefore < caNotBefore) notBefore = caNotBefore;   // never predate the issuer
        var requestedNotAfter = now.AddDays(opts.ValidityDays);
        var caNotAfter = new DateTimeOffset(caCert.NotAfter.ToUniversalTime());
        var notAfter = requestedNotAfter < caNotAfter ? requestedNotAfter : caNotAfter;
        if (notAfter <= notBefore)
            throw new CertConvertException(
                "The CA certificate has too little validity remaining to issue a certificate.");

        byte[] serial;
        do
        {
            serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;   // positive DER integer
        } while (serial.All(b => b == 0));

        // --- 5. Sign with the CA key. Use the explicit signature generator: the fresh
        //        request was built from a PublicKey, so it carries no RSA padding, and
        //        the issuer-cert overload can't infer one for an RSA CA. --------------
        X509SignatureGenerator generator = caKey switch
        {
            RSA rsa => X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1),
            ECDsa ec => X509SignatureGenerator.CreateForECDsa(ec),
            _ => throw new CertConvertException("Unsupported CA key type."),
        };
        return request.Create(caCert.SubjectName, generator, notBefore, notAfter, serial);
    }

    private static void AddSubjectAltNames(
        CertificateRequest request, X500DistinguishedName subject, IssuanceOptions opts)
    {
        var san = new SubjectAlternativeNameBuilder();
        int count = 0;

        foreach (var d in Clean(opts.DnsNames))
        {
            // A leading "*." wildcard is legal; check the rest as a host name.
            string host = d.StartsWith("*.") ? d[2..] : d;
            if (host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw new CertConvertException($"Invalid DNS name in the subject alternative names: \"{d}\".");
            san.AddDnsName(d); count++;
        }
        foreach (var ip in Clean(opts.IpAddresses))
        {
            if (!IPAddress.TryParse(ip, out var addr))
                throw new CertConvertException($"Invalid IP address in the subject alternative names: \"{ip}\".");
            san.AddIpAddress(addr); count++;
        }
        foreach (var email in Clean(opts.EmailNames))
        {
            int at = email.IndexOf('@');
            if (at <= 0 || at >= email.Length - 1 || email.IndexOf('@', at + 1) >= 0)
                throw new CertConvertException($"Invalid email address in the subject alternative names: \"{email}\".");
            san.AddEmailAddress(email); count++;
        }
        // URIs dedup case-sensitively — unlike host names, a URI's path and query are
        // case-significant (RFC 3986), so two URIs differing only in case are distinct.
        foreach (var uri in Clean(opts.Uris, StringComparer.Ordinal))
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                throw new CertConvertException($"Invalid URI in the subject alternative names: \"{uri}\".");
            san.AddUri(parsed); count++;
        }

        bool emptySubject = string.IsNullOrEmpty(subject.Name);
        if (count == 0)
        {
            // A certificate with no subject AND no SAN has no identity — RFC 5280 forbids it.
            if (emptySubject)
                throw new CertConvertException(
                    "The request has an empty subject and no subject alternative names were supplied — "
                    + "the issued certificate would have no identity. Provide at least one SAN.");
            return;
        }
        // RFC 5280: if the subject DN is empty, the SAN extension must be critical.
        request.CertificateExtensions.Add(san.Build(emptySubject));
    }

    private static IEnumerable<string> Clean(IReadOnlyList<string> values, StringComparer? comparer = null) =>
        values.Select(v => v.Trim())
              .Where(v => v.Length > 0)
              .Distinct(comparer ?? StringComparer.OrdinalIgnoreCase);
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

/// <summary>What a loaded file turned out to contain.</summary>
public enum ContentKind
{
    Certificates,
    Pkcs12,
    PrivateKey,
    CertificateRequest,
}

/// <summary>Target formats for certificate export.</summary>
public enum CertOutputFormat
{
    Pem,       // one or more CERTIFICATE blocks
    Der,       // single certificate, binary
    Pkcs7Pem,  // PKCS #7 certs-only bundle, PEM armour
    Pkcs7Der,  // PKCS #7 certs-only bundle, binary
    Pkcs12,    // PFX/P12, optionally with private key
}

/// <summary>Target formats for private key export.</summary>
public enum KeyOutputFormat
{
    Pkcs8Pem,          // -----BEGIN PRIVATE KEY-----
    Pkcs8Der,
    Pkcs8EncryptedPem, // -----BEGIN ENCRYPTED PRIVATE KEY-----
    Pkcs1Pem,          // -----BEGIN RSA PRIVATE KEY----- (RSA only)
    Sec1Pem,           // -----BEGIN EC PRIVATE KEY----- (EC only)
}

/// <summary>PKCS #12 encryption profile.</summary>
public enum Pkcs12Encryption
{
    /// <summary>AES-256-CBC with PBKDF2/SHA-256 — matches modern OpenSSL 3.x defaults.</summary>
    Modern,
    /// <summary>3DES with SHA-1 — maximum compatibility with old Windows/Java imports.</summary>
    Legacy,
}

/// <summary>A private key together with display metadata.</summary>
public sealed class PrivateKeyEntry : IDisposable
{
    public required AsymmetricAlgorithm Key { get; init; }
    /// <summary>"RSA" or "ECDSA".</summary>
    public required string Algorithm { get; init; }
    /// <summary>Bits for RSA; field size for EC.</summary>
    public required int KeySize { get; init; }
    /// <summary>Curve name for EC keys, e.g. "nistP256".</summary>
    public string? Curve { get; init; }

    public string Description =>
        Curve is null ? $"{Algorithm} {KeySize}-bit" : $"{Algorithm} {Curve}";

    public void Dispose() => Key.Dispose();
}

/// <summary>Result of loading and auto-detecting any supported input.</summary>
public sealed class LoadedContent : IDisposable
{
    public required ContentKind Kind { get; init; }
    /// <summary>Human description of what was detected, e.g. "PEM (3 certificates)".</summary>
    public required string SourceDescription { get; init; }
    public List<X509Certificate2> Certificates { get; } = new();
    public List<PrivateKeyEntry> PrivateKeys { get; } = new();
    /// <summary>PKCS #10 requests in PEM form, when the input contained any.</summary>
    public List<string> CertificateRequests { get; } = new();

    /// <summary>The first certificate request in PEM form, or null. Read-only
    /// convenience over <see cref="CertificateRequests"/>; this replaced a settable
    /// property whose only writer was the content loader, which now appends to the
    /// list. Writers must use <see cref="CertificateRequests"/>.</summary>
    public string? CertificateRequestPem =>
        CertificateRequests.Count > 0 ? CertificateRequests[0] : null;

    public void Dispose()
    {
        foreach (var c in Certificates) c.Dispose();
        foreach (var k in PrivateKeys) k.Dispose();
    }
}

/// <summary>Everything the inspector shows about one certificate.</summary>
public sealed record CertificateInfo
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required string SerialNumber { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
    public required int Version { get; init; }
    public required string KeyAlgorithm { get; init; }
    public required string SignatureAlgorithm { get; init; }
    public required string Sha1Fingerprint { get; init; }
    public required string Sha256Fingerprint { get; init; }
    public required bool IsSelfSigned { get; init; }
    /// <summary>The signature verifies against the certificate's own public key —
    /// genuinely self-signed, not merely self-issued (matching subject/issuer DNs).</summary>
    public bool IsSelfSignatureValid { get; init; }
    public bool IsCertificateAuthority { get; init; }
    public bool HasBasicConstraints { get; init; }
    public int? PathLengthConstraint { get; init; }
    public IReadOnlyList<string> SubjectAlternativeNames { get; init; } = [];
    public IReadOnlyList<string> KeyUsages { get; init; } = [];
    public IReadOnlyList<string> EnhancedKeyUsages { get; init; } = [];
    public string? SubjectKeyIdentifier { get; init; }
    public string? AuthorityKeyIdentifier { get; init; }

    // Structured fields for locale-proof analysis (the health check). Additive —
    // populated only by Inspector.Inspect, so no existing construction site breaks.
    /// <summary>"RSA", "ECDSA", or the raw key OID.</summary>
    public string PublicKeyAlgorithm { get; init; } = "";
    /// <summary>Modulus bits for RSA, field size for EC, 0 if unknown.</summary>
    public int PublicKeyBits { get; init; }
    /// <summary>Signature algorithm OID — match on this, never the localised friendly name.</summary>
    public string SignatureAlgorithmOid { get; init; } = "";
    public X509KeyUsageFlags KeyUsageFlags { get; init; }
    public IReadOnlyList<string> EnhancedKeyUsageOids { get; init; } = [];

    public bool IsExpired => DateTimeOffset.UtcNow > NotAfter;
    public bool IsNotYetValid => DateTimeOffset.UtcNow < NotBefore;

    /// <summary>Short one-line identity, preferring the CN.</summary>
    public string DisplayName => Inspector.CommonNameOf(Subject);
}

/// <summary>Whether a certificate request's own proof-of-possession signature verified.</summary>
public enum CsrSignatureState
{
    /// <summary>The self-signature verified against the requested public key.</summary>
    Valid,
    /// <summary>The request parsed, but its self-signature does not verify.</summary>
    Invalid,
    /// <summary>The signature was not checked (should not occur in normal use).</summary>
    NotChecked,
}

/// <summary>
/// Everything the inspector shows about one PKCS #10 certificate signing request.
/// Every field describes what the requester <em>asked for</em>, not asserted fact:
/// the extensions are attacker-controlled and must never be fed into a signing path
/// without independent policy checks (the safety tripwire for a future signing CA).
/// </summary>
public sealed record CsrInfo
{
    public required string Subject { get; init; }
    public required string KeyAlgorithm { get; init; }
    public required string PublicKeyAlgorithm { get; init; }
    public required int PublicKeyBits { get; init; }
    public required string SignatureAlgorithm { get; init; }
    public required string SignatureAlgorithmOid { get; init; }
    public required CsrSignatureState SignatureState { get; init; }
    public required string Sha256Fingerprint { get; init; }
    /// <summary>SubjectPublicKeyInfo of the requested key — used to match a private key to the request.</summary>
    public required byte[] PublicKeySpki { get; init; }
    public bool HasBasicConstraints { get; init; }
    public bool RequestsCa { get; init; }
    public int? PathLengthConstraint { get; init; }
    public IReadOnlyList<string> SubjectAlternativeNames { get; init; } = [];
    public IReadOnlyList<string> KeyUsages { get; init; } = [];
    public IReadOnlyList<string> EnhancedKeyUsages { get; init; } = [];
    public X509KeyUsageFlags KeyUsageFlags { get; init; }
    public IReadOnlyList<string> EnhancedKeyUsageOids { get; init; } = [];
    /// <summary>OIDs of any requested extensions that could not be parsed.</summary>
    public IReadOnlyList<string> UnreadableExtensions { get; init; } = [];

    /// <summary>Short one-line identity, preferring the CN.</summary>
    public string DisplayName => Inspector.CommonNameOf(Subject);
}

/// <summary>Validation outcome for a single certificate in a chain.</summary>
public sealed record ChainElementResult
{
    public required CertificateInfo Certificate { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public bool IsOk => Issues.Count == 0;
}

/// <summary>Outcome of validating an ordered chain.</summary>
public sealed record ChainValidationResult
{
    public required bool IsValid { get; init; }
    /// <summary>Leaf first, root last — as validated.</summary>
    public required IReadOnlyList<ChainElementResult> Elements { get; init; }
    /// <summary>Chain-wide notes, e.g. "root not supplied".</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

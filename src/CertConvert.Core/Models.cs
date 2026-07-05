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
    /// <summary>PKCS #10 request in PEM form, when the input contained a CSR.</summary>
    public string? CertificateRequestPem { get; set; }

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
    public bool IsCertificateAuthority { get; init; }
    public bool HasBasicConstraints { get; init; }
    public int? PathLengthConstraint { get; init; }
    public IReadOnlyList<string> SubjectAlternativeNames { get; init; } = [];
    public IReadOnlyList<string> KeyUsages { get; init; } = [];
    public IReadOnlyList<string> EnhancedKeyUsages { get; init; } = [];
    public string? SubjectKeyIdentifier { get; init; }
    public string? AuthorityKeyIdentifier { get; init; }

    public bool IsExpired => DateTimeOffset.UtcNow > NotAfter;
    public bool IsNotYetValid => DateTimeOffset.UtcNow < NotBefore;

    /// <summary>Short one-line identity, preferring the CN.</summary>
    public string DisplayName
    {
        get
        {
            foreach (var part in Subject.Split(',', StringSplitOptions.TrimEntries))
                if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    return part[3..];
            return Subject.Length > 0 ? Subject : "(no subject)";
        }
    }
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

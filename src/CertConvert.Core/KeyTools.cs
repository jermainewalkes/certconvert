using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core;

public static class KeyTools
{
    private const string RsaOid = "1.2.840.113549.1.1.1";
    private const string EcOid = "1.2.840.10045.2.1";

    /// <summary>PBE used whenever this tool writes an encrypted PKCS #8 key.</summary>
    public static readonly PbeParameters ModernPbe =
        new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>Wraps a raw key object with display metadata.</summary>
    public static PrivateKeyEntry Describe(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa => new PrivateKeyEntry { Key = rsa, Algorithm = "RSA", KeySize = rsa.KeySize },
        ECDsa ec => new PrivateKeyEntry
        {
            Key = ec,
            Algorithm = "ECDSA",
            KeySize = ec.KeySize,
            Curve = SafeCurveName(ec),
        },
        _ => throw new CertConvertException(
            $"Unsupported key algorithm: {key.GetType().Name}. RSA and ECDSA keys are supported."),
    };

    private static string? SafeCurveName(ECDsa ec)
    {
        try { return ec.ExportParameters(false).Curve.Oid?.FriendlyName; }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Imports an unencrypted PKCS #8 DER key, detecting RSA vs EC from the algorithm OID.</summary>
    public static PrivateKeyEntry ImportPkcs8Der(ReadOnlyMemory<byte> der)
    {
        Pkcs8PrivateKeyInfo info;
        try
        {
            info = Pkcs8PrivateKeyInfo.Decode(der, out _, skipCopy: true);
        }
        catch (CryptographicException e)
        {
            throw new UnrecognisedContentException($"Not a valid PKCS #8 private key: {e.Message}");
        }

        switch (info.AlgorithmId.Value)
        {
            case RsaOid:
            {
                // Decode() only validated the PKCS #8 envelope; the inner key can
                // still be malformed, so ImportPkcs8PrivateKey may throw. Surface
                // that as a clean error and don't leak the key handle.
                var rsa = RSA.Create();
                try { rsa.ImportPkcs8PrivateKey(der.Span, out _); }
                catch (CryptographicException e)
                {
                    rsa.Dispose();
                    throw new UnrecognisedContentException($"Not a valid RSA private key: {e.Message}");
                }
                return Describe(rsa);
            }
            case EcOid:
            {
                var ec = ECDsa.Create();
                try { ec.ImportPkcs8PrivateKey(der.Span, out _); }
                catch (CryptographicException e)
                {
                    ec.Dispose();
                    throw new UnrecognisedContentException($"Not a valid ECDSA private key: {e.Message}");
                }
                return Describe(ec);
            }
            default:
                throw new CertConvertException(
                    $"Unsupported private key algorithm OID {info.AlgorithmId.Value}. " +
                    "RSA and ECDSA keys are supported.");
        }
    }

    /// <summary>Loads any private-key PEM block this tool understands.</summary>
    public static PrivateKeyEntry LoadPemBlock(PemBlock block, string? password)
    {
        switch (block.Label)
        {
            case "PRIVATE KEY":
                return ImportPkcs8Der(block.Der);

            case "ENCRYPTED PRIVATE KEY":
            {
                if (password is null)
                    throw new PasswordRequiredException("The private key");
                Pkcs8PrivateKeyInfo info;
                try
                {
                    info = Pkcs8PrivateKeyInfo.DecryptAndDecode(password, block.Der, out _);
                }
                catch (CryptographicException)
                {
                    throw new InvalidPasswordException("the encrypted private key");
                }
                return ImportPkcs8Der(info.Encode());
            }

            case "RSA PRIVATE KEY":
            {
                byte[] der = DecryptIfLegacy(block, password);
                var rsa = RSA.Create();
                try
                {
                    rsa.ImportRSAPrivateKey(der, out _);
                }
                catch (CryptographicException e)
                {
                    rsa.Dispose();
                    throw new UnrecognisedContentException($"Not a valid PKCS #1 RSA key: {e.Message}");
                }
                return Describe(rsa);
            }

            case "EC PRIVATE KEY":
            {
                byte[] der = DecryptIfLegacy(block, password);
                var ec = ECDsa.Create();
                try
                {
                    ec.ImportECPrivateKey(der, out _);
                }
                catch (CryptographicException e)
                {
                    ec.Dispose();
                    throw new UnrecognisedContentException($"Not a valid SEC 1 EC key: {e.Message}");
                }
                return Describe(ec);
            }

            default:
                throw new UnrecognisedContentException($"Unsupported PEM label \"{block.Label}\".");
        }
    }

    private static byte[] DecryptIfLegacy(PemBlock block, string? password)
    {
        if (!block.IsLegacyEncrypted) return block.Der;
        if (password is null)
            throw new PasswordRequiredException("The private key");
        return PemUtil.DecryptLegacyPemBody(block, password);
    }

    /// <summary>Exports a key as PEM text in the requested format.</summary>
    public static string ExportPem(AsymmetricAlgorithm key, KeyOutputFormat format, string? password = null)
    {
        switch (format)
        {
            case KeyOutputFormat.Pkcs8Pem:
                return PemUtil.Encode("PRIVATE KEY", key.ExportPkcs8PrivateKey());

            case KeyOutputFormat.Pkcs8EncryptedPem:
                if (string.IsNullOrEmpty(password))
                    throw new CertConvertException("A password is required to export an encrypted key.");
                return PemUtil.Encode("ENCRYPTED PRIVATE KEY",
                    key.ExportEncryptedPkcs8PrivateKey(password, ModernPbe));

            case KeyOutputFormat.Pkcs1Pem:
                if (key is not RSA rsa)
                    throw new CertConvertException("PKCS #1 format applies to RSA keys only.");
                return PemUtil.Encode("RSA PRIVATE KEY", rsa.ExportRSAPrivateKey());

            case KeyOutputFormat.Sec1Pem:
                if (key is not ECDsa ec)
                    throw new CertConvertException("SEC 1 format applies to EC keys only.");
                return PemUtil.Encode("EC PRIVATE KEY", ec.ExportECPrivateKey());

            case KeyOutputFormat.Pkcs8Der:
                throw new CertConvertException("Use ExportDer for binary output.");

            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static byte[] ExportDer(AsymmetricAlgorithm key) => key.ExportPkcs8PrivateKey();

    /// <summary>True when the private key matches the certificate's public key.</summary>
    public static bool Matches(X509Certificate2 cert, AsymmetricAlgorithm key)
    {
        byte[] keySpki = key switch
        {
            RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
            ECDsa ec => ec.ExportSubjectPublicKeyInfo(),
            _ => throw new CertConvertException(
                $"Unsupported key algorithm: {key.GetType().Name}."),
        };
        return cert.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(keySpki);
    }
}

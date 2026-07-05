using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CertConvert.Core;

/// <summary>Loads any supported input and auto-detects what it is.</summary>
public static class ContentLoader
{
    /// <summary>Certificate material is kilobytes; anything huge is a mistake (or a memory bomb).</summary>
    private const long MaxInputBytes = 10 * 1024 * 1024;

    public static LoadedContent LoadFile(string path, string? password = null)
    {
        string name = Path.GetFileName(path);
        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxInputBytes)
                throw new UnrecognisedContentException(
                    $"{name} is {info.Length / (1024.0 * 1024.0):F0} MB — certificate files are a few " +
                    "kilobytes, so this is almost certainly not one. Files over 10 MB are refused.");
            data = File.ReadAllBytes(path);
        }
        catch (IOException e)
        {
            throw new CertConvertException($"Cannot read {path}: {e.Message}", e);
        }
        if (data.Length == 0)
            throw new UnrecognisedContentException($"{name} is empty.");

        // Attach the file name so multi-file operations name the offender.
        try
        {
            return Load(data, password);
        }
        catch (PasswordRequiredException e)
        {
            throw new PasswordRequiredException(name, e);
        }
        catch (InvalidPasswordException e)
        {
            throw new InvalidPasswordException(name, e);
        }
        catch (UnrecognisedContentException e)
        {
            throw new UnrecognisedContentException($"{name}: {e.Message}");
        }
        catch (CertConvertException e)
        {
            throw new CertConvertException($"{name}: {e.Message}", e);
        }
    }

    public static LoadedContent Load(byte[] data, string? password = null) =>
        PemUtil.LooksLikePem(data)
            ? LoadPem(Encoding.UTF8.GetString(data), password)
            : LoadBinary(data, password);

    private static LoadedContent LoadPem(string text, string? password)
    {
        var blocks = PemUtil.ParseAll(text);
        if (blocks.Count == 0)
            throw new UnrecognisedContentException("No PEM blocks found in the input.");

        var certs = new List<X509Certificate2>();
        var keys = new List<PrivateKeyEntry>();
        string? csrPem = null;
        var skipped = new List<string>();

        foreach (var block in blocks)
        {
            switch (block.Label)
            {
                case "CERTIFICATE":
                case "X509 CERTIFICATE":
                    certs.Add(X509CertificateLoader.LoadCertificate(block.Der));
                    break;

                case "PKCS7":
                case "PKCS #7 SIGNED DATA":
                    certs.AddRange(Pkcs7Util.ReadCertificates(block.Der));
                    break;

                case "PRIVATE KEY":
                case "ENCRYPTED PRIVATE KEY":
                case "RSA PRIVATE KEY":
                case "EC PRIVATE KEY":
                    keys.Add(KeyTools.LoadPemBlock(block, password));
                    break;

                case "CERTIFICATE REQUEST":
                case "NEW CERTIFICATE REQUEST":
                    csrPem = PemUtil.Encode("CERTIFICATE REQUEST", block.Der);
                    break;

                default:
                    skipped.Add(block.Label);
                    break;
            }
        }

        if (certs.Count == 0 && keys.Count == 0 && csrPem is null)
            throw new UnrecognisedContentException(
                $"No usable PEM content found. Blocks present: {string.Join(", ", skipped)}.");

        ContentKind kind = certs.Count > 0 ? ContentKind.Certificates
            : keys.Count > 0 ? ContentKind.PrivateKey
            : ContentKind.CertificateRequest;

        var parts = new List<string>();
        if (certs.Count > 0)
            parts.Add(certs.Count == 1 ? "1 certificate" : $"{certs.Count} certificates");
        if (keys.Count > 0)
            parts.Add(keys.Count == 1 ? "1 private key" : $"{keys.Count} private keys");
        if (csrPem is not null)
            parts.Add("1 certificate request");
        if (skipped.Count > 0)
            parts.Add($"ignored: {string.Join(", ", skipped)}");

        var content = new LoadedContent
        {
            Kind = kind,
            SourceDescription = $"PEM ({string.Join(", ", parts)})",
            CertificateRequestPem = csrPem,
        };
        content.Certificates.AddRange(certs);
        content.PrivateKeys.AddRange(keys);
        return content;
    }

    private static LoadedContent LoadBinary(byte[] data, string? password)
    {
        // 1. Single DER certificate
        try
        {
            var cert = X509CertificateLoader.LoadCertificate(data);
            var c = new LoadedContent
            {
                Kind = ContentKind.Certificates,
                SourceDescription = "DER (1 certificate)",
            };
            c.Certificates.Add(cert);
            return c;
        }
        catch (CryptographicException) { }

        // 2. PKCS #7 bundle
        try
        {
            var cms = new SignedCms();
            cms.Decode(data);
            if (cms.Certificates.Count > 0)
            {
                var c = new LoadedContent
                {
                    Kind = ContentKind.Certificates,
                    SourceDescription = $"PKCS #7 ({cms.Certificates.Count} certificate{(cms.Certificates.Count == 1 ? "" : "s")})",
                };
                c.Certificates.AddRange(cms.Certificates);
                return c;
            }
        }
        catch (CryptographicException) { }

        // 3. PKCS #12 — may legitimately demand a password
        bool looksLikePkcs12 = false;
        try
        {
            Pkcs12Info.Decode(data, out _, skipCopy: true);
            looksLikePkcs12 = true;
        }
        catch (CryptographicException) { }
        if (looksLikePkcs12)
        {
            var (certs, keys) = Pkcs12Util.Read(data, password);
            var c = new LoadedContent
            {
                Kind = ContentKind.Pkcs12,
                SourceDescription = $"PKCS #12 ({certs.Count} certificate{(certs.Count == 1 ? "" : "s")}, " +
                                    $"{keys.Count} key{(keys.Count == 1 ? "" : "s")})",
            };
            c.Certificates.AddRange(certs);
            c.PrivateKeys.AddRange(keys);
            return c;
        }

        // 4. Unencrypted PKCS #8 key
        try
        {
            var entry = KeyTools.ImportPkcs8Der(data);
            var c = new LoadedContent
            {
                Kind = ContentKind.PrivateKey,
                SourceDescription = $"DER PKCS #8 ({entry.Description})",
            };
            c.PrivateKeys.Add(entry);
            return c;
        }
        catch (CertConvertException) { }

        // 5. PKCS #1 RSA key
        var rsa = RSA.Create();
        try
        {
            rsa.ImportRSAPrivateKey(data, out _);
            var c = new LoadedContent
            {
                Kind = ContentKind.PrivateKey,
                SourceDescription = "DER PKCS #1 (RSA private key)",
            };
            c.PrivateKeys.Add(KeyTools.Describe(rsa));
            return c;
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
        }

        throw new UnrecognisedContentException(
            "The input is not a recognisable certificate, key, PKCS #7 or PKCS #12 file.");
    }
}

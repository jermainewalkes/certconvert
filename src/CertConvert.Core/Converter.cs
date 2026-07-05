using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CertConvert.Core;

public sealed class ExportOptions
{
    /// <summary>Password for PKCS #12 output. Empty is allowed but discouraged.</summary>
    public string? Password { get; set; }
    /// <summary>Private key to embed in PKCS #12 output.</summary>
    public AsymmetricAlgorithm? PrivateKey { get; set; }
    public Pkcs12Encryption Encryption { get; set; } = Pkcs12Encryption.Modern;
}

/// <summary>Converts certificates (and bundles) between the supported output formats.</summary>
public static class Converter
{
    public static byte[] Export(
        IReadOnlyList<X509Certificate2> certs,
        CertOutputFormat format,
        ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        if (certs.Count == 0 && format != CertOutputFormat.Pkcs12)
            throw new CertConvertException("No certificates to export.");

        switch (format)
        {
            case CertOutputFormat.Pem:
            {
                var sb = new StringBuilder();
                foreach (var cert in certs)
                    sb.Append(cert.ExportCertificatePem()).Append('\n');
                return Encoding.ASCII.GetBytes(sb.ToString());
            }

            case CertOutputFormat.Der:
                if (certs.Count > 1)
                    throw new CertConvertException(
                        $"DER holds a single certificate but {certs.Count} were supplied. " +
                        "Export as PEM or PKCS #7 to keep the whole chain, or select one certificate.");
                return certs[0].RawData;

            case CertOutputFormat.Pkcs7Der:
                return Pkcs7Util.Write(certs);

            case CertOutputFormat.Pkcs7Pem:
                return Encoding.ASCII.GetBytes(
                    PemUtil.Encode("PKCS7", Pkcs7Util.Write(certs)));

            case CertOutputFormat.Pkcs12:
                return Pkcs12Util.Write(
                    certs, options.PrivateKey, options.Password ?? "", options.Encryption);

            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>Guesses the output format from a file extension; null when ambiguous.</summary>
    public static CertOutputFormat? GuessFormatFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pem" or ".crt" => CertOutputFormat.Pem,
            ".cer" or ".der" => CertOutputFormat.Der,
            ".p7b" or ".p7c" => CertOutputFormat.Pkcs7Der,
            ".pfx" or ".p12" => CertOutputFormat.Pkcs12,
            _ => null,
        };
}

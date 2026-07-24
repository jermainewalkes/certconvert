using System.Security.Cryptography;
using System.Text;

namespace CertConvert.Core;

/// <summary>One decoded PEM block, including any RFC 1421 headers (legacy encrypted keys).</summary>
public sealed record PemBlock
{
    public required string Label { get; init; }
    public required byte[] Der { get; init; }
    /// <summary>Headers such as Proc-Type / DEK-Info found between BEGIN and the base64 body.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();

    public bool IsLegacyEncrypted =>
        Headers.TryGetValue("Proc-Type", out var v) && v.Contains("ENCRYPTED", StringComparison.OrdinalIgnoreCase);
}

public static class PemUtil
{
    /// <summary>True if the data looks like text containing at least one PEM block.</summary>
    public static bool LooksLikePem(ReadOnlySpan<byte> data)
    {
        // PEM is ASCII; check a generous prefix for the BEGIN marker.
        var probe = Encoding.ASCII.GetString(data[..Math.Min(data.Length, 4096)]);
        return probe.Contains("-----BEGIN ");
    }

    /// <summary>
    /// Parses every PEM block in the text, tolerating RFC 1421 headers (Proc-Type/DEK-Info)
    /// that the strict <see cref="PemEncoding"/> API rejects.
    /// </summary>
    public static List<PemBlock> ParseAll(string text)
    {
        var blocks = new List<PemBlock>();
        int pos = 0;
        while (true)
        {
            int begin = text.IndexOf("-----BEGIN ", pos, StringComparison.Ordinal);
            if (begin < 0) break;
            int labelEnd = text.IndexOf("-----", begin + 11, StringComparison.Ordinal);
            if (labelEnd < 0) break;
            string label = text[(begin + 11)..labelEnd].Trim();

            string endMarker = $"-----END {label}-----";
            int end = text.IndexOf(endMarker, labelEnd, StringComparison.Ordinal);
            if (end < 0)
                throw new UnrecognisedContentException(
                    $"PEM block \"{label}\" has no matching END marker.");

            string body = text[(labelEnd + 5)..end];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var base64 = new StringBuilder();
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                // Header lines only appear before base64 content and contain a colon.
                if (colon > 0 && base64.Length == 0 && !IsBase64Line(line))
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                else
                    base64.Append(line);
            }

            byte[] der;
            try
            {
                der = Convert.FromBase64String(base64.ToString());
            }
            catch (FormatException e)
            {
                throw new UnrecognisedContentException(
                    $"PEM block \"{label}\" contains invalid base64.") { Source = e.Source };
            }

            blocks.Add(new PemBlock { Label = label, Der = der, Headers = headers });
            pos = end + endMarker.Length;
        }
        return blocks;
    }

    private static bool IsBase64Line(string line) =>
        line.Length >= 40 && !line.Contains(' ') && line.IndexOf(':') < 0;

    /// <summary>Wraps DER bytes in PEM armour with the given label.</summary>
    public static string Encode(string label, ReadOnlySpan<byte> der) =>
        new string(PemEncoding.Write(label, der)) + "\n";

    /// <summary>
    /// Decrypts a legacy OpenSSL-encrypted PEM body (Proc-Type: 4,ENCRYPTED + DEK-Info)
    /// into plain DER. Supports DES-EDE3-CBC and AES-128/192/256-CBC.
    /// </summary>
    public static byte[] DecryptLegacyPemBody(PemBlock block, string password)
    {
        if (!block.Headers.TryGetValue("DEK-Info", out var dekInfo))
            throw new UnrecognisedContentException(
                "Encrypted PEM block is missing its DEK-Info header.");

        var parts = dekInfo.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            throw new UnrecognisedContentException($"Malformed DEK-Info header: \"{dekInfo}\".");

        string cipherName = parts[0].ToUpperInvariant();
        // The IV is attacker-controllable hex from a file header: reject odd/non-hex
        // and too-short values cleanly rather than crashing on FromHexString /
        // AsSpan(0, 8) below (EvpBytesToKey salts on the first 8 IV bytes).
        byte[] iv;
        try { iv = Convert.FromHexString(parts[1]); }
        catch (FormatException)
        {
            throw new UnrecognisedContentException($"Malformed DEK-Info IV: \"{parts[1]}\".");
        }
        if (iv.Length < 8)
            throw new UnrecognisedContentException(
                $"DEK-Info IV is too short ({iv.Length} bytes; at least 8 required).");

        int keyLen = cipherName switch
        {
            "DES-EDE3-CBC" => 24,
            "AES-128-CBC" => 16,
            "AES-192-CBC" => 24,
            "AES-256-CBC" => 32,
            _ => throw new UnrecognisedContentException(
                $"Unsupported legacy PEM cipher \"{cipherName}\". " +
                "Convert the key with a modern tool first, or use PKCS #8."),
        };

        byte[] key = EvpBytesToKey(password, iv.AsSpan(0, 8).ToArray(), keyLen);
        try
        {
            using SymmetricAlgorithm alg = cipherName == "DES-EDE3-CBC"
                ? TripleDES.Create()
                : Aes.Create();
            alg.Mode = CipherMode.CBC;
            alg.Padding = PaddingMode.PKCS7;
            alg.Key = key;
            alg.IV = iv;
            using var dec = alg.CreateDecryptor();
            return dec.TransformFinalBlock(block.Der, 0, block.Der.Length);
        }
        catch (CryptographicException)
        {
            throw new InvalidPasswordException("the encrypted private key");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>OpenSSL's EVP_BytesToKey KDF (MD5, one iteration) used by legacy PEM encryption.</summary>
    private static byte[] EvpBytesToKey(string password, byte[] salt, int keyLen)
    {
        byte[] pass = Encoding.UTF8.GetBytes(password);
        var key = new List<byte>(keyLen);
        byte[] prev = [];
        while (key.Count < keyLen)
        {
            byte[] input = new byte[prev.Length + pass.Length + salt.Length];
            prev.CopyTo(input, 0);
            pass.CopyTo(input, prev.Length);
            salt.CopyTo(input, prev.Length + pass.Length);
#pragma warning disable CA5351 // MD5 is mandated by the legacy OpenSSL format, not a choice.
            prev = MD5.HashData(input);
#pragma warning restore CA5351
            key.AddRange(prev);
            CryptographicOperations.ZeroMemory(input);
        }
        CryptographicOperations.ZeroMemory(pass);
        return key.Take(keyLen).ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CertConvert.Core;

namespace CertConvert.Cli;

internal static class Commands
{
    // ---------- inspect ----------

    public static int Inspect(string[] args)
    {
        var opts = new ArgReader(args);
        if (opts.Positionals.Count != 1)
            return CliRunner.Usage("Usage: certconvert inspect <file> [--password <pw>]");

        using var content = ContentLoader.LoadFile(opts.Positionals[0], opts.Get("--password"));
        Console.WriteLine($"Detected: {content.SourceDescription}");
        Console.WriteLine();

        for (int i = 0; i < content.Certificates.Count; i++)
        {
            if (content.Certificates.Count > 1)
                Console.WriteLine($"--- Certificate {i + 1} of {content.Certificates.Count} ---");
            Console.Write(Inspector.ToText(Inspector.Inspect(content.Certificates[i])));
            Console.WriteLine();
        }
        foreach (var key in content.PrivateKeys)
            Console.WriteLine($"Private key:   {key.Description}");
        if (content.CertificateRequestPem is not null)
            Console.WriteLine("Certificate request (CSR) present.");
        return CliRunner.ExitOk;
    }

    // ---------- convert ----------

    public static int Convert(string[] args)
    {
        var opts = new ArgReader(args, "--legacy");
        if (opts.Positionals.Count == 0)
            return CliRunner.Usage("Usage: certconvert convert <in>... -o <out> [--to <format>]");
        string outPath = opts.Require("--out", "Output file");
        var format = ResolveFormat(opts.Get("--to"), outPath);

        var (certs, keys) = LoadInputs(opts);
        try
        {
            if (certs.Count == 0 && keys.Count > 0)
                throw new CertConvertException(format == CertOutputFormat.Pkcs12
                    ? "Only private keys were supplied. Add the certificate the key belongs " +
                      "to — a PFX pairs certificates with their key."
                    : "The input holds only private keys — use \"certconvert key convert\" for keys.");

            ExportCertificates(certs, keys, outPath, format, opts);
        }
        finally
        {
            DisposeAll(certs, keys);
        }
        return CliRunner.ExitOk;
    }

    // ---------- chain ----------

    public static int ChainBuild(string[] args)
    {
        var opts = new ArgReader(args, "--legacy");
        if (opts.Positionals.Count == 0)
            return CliRunner.Usage("Usage: certconvert chain build <files>... -o <out>");
        string outPath = opts.Require("--out", "Output file");

        var format = ResolveFormat(opts.Get("--to"), outPath);
        var (certs, keys) = LoadInputs(opts);
        try
        {
            var ordered = ChainTools.Order(certs);
            Console.WriteLine("Chain order (leaf first):");
            foreach (var cert in ordered)
                Console.WriteLine($"  {Inspector.Inspect(cert).DisplayName}");

            ExportCertificates(ordered, keys, outPath, format, opts);
        }
        finally
        {
            DisposeAll(certs, keys);
        }
        return CliRunner.ExitOk;
    }

    public static int ChainVerify(string[] args)
    {
        var opts = new ArgReader(args, "--system-roots");
        if (opts.Positionals.Count == 0)
            return CliRunner.Usage("Usage: certconvert chain verify <files>... [--system-roots]");

        var (certs, keys) = LoadInputs(opts);
        try
        {
            var result = ChainTools.Validate(certs, opts.Has("--system-roots"));
            foreach (var note in result.Notes)
                Console.WriteLine($"Note: {note}");

            foreach (var element in result.Elements)
            {
                string status = element.IsOk ? "OK " : "FAIL";
                Console.WriteLine($"  [{status}] {element.Certificate.DisplayName}");
                foreach (var issue in element.Issues)
                    Console.WriteLine($"         - {issue}");
            }
            Console.WriteLine(result.IsValid ? "Chain is valid." : "Chain is NOT valid.");
            return result.IsValid ? CliRunner.ExitOk : CliRunner.ExitFailure;
        }
        finally
        {
            DisposeAll(certs, keys);
        }
    }

    // ---------- key ----------

    public static int KeyConvert(string[] args)
    {
        var opts = new ArgReader(args);
        if (opts.Positionals.Count != 1)
            return CliRunner.Usage("Usage: certconvert key convert <in> -o <out> [--to <format>]");
        string outPath = opts.Require("--out", "Output file");

        using var content = ContentLoader.LoadFile(opts.Positionals[0], opts.Get("--password"));
        if (content.PrivateKeys.Count == 0)
            throw new CertConvertException("No private key found in the input.");
        var key = content.PrivateKeys[0].Key;

        string to = opts.Get("--to")?.ToLowerInvariant()
            ?? (Path.GetExtension(outPath).ToLowerInvariant() == ".der" ? "der" : "pkcs8");
        string? outPassword = opts.Get("--out-password");

        byte[] bytes = to switch
        {
            "pkcs8" => Encoding.ASCII.GetBytes(KeyTools.ExportPem(key, KeyOutputFormat.Pkcs8Pem)),
            "pkcs8-enc" => Encoding.ASCII.GetBytes(KeyTools.ExportPem(
                key, KeyOutputFormat.Pkcs8EncryptedPem,
                outPassword ?? throw new CertConvertException(
                    "--out-password is required for encrypted output."))),
            "pkcs1" => Encoding.ASCII.GetBytes(KeyTools.ExportPem(key, KeyOutputFormat.Pkcs1Pem)),
            "sec1" => Encoding.ASCII.GetBytes(KeyTools.ExportPem(key, KeyOutputFormat.Sec1Pem)),
            "der" => KeyTools.ExportDer(key),
            _ => throw new CertConvertException(
                $"Unknown key format \"{to}\". Use pkcs8, pkcs8-enc, pkcs1, sec1 or der."),
        };
        WriteFile(outPath, bytes);
        return CliRunner.ExitOk;
    }

    public static int KeyMatch(string[] args)
    {
        var opts = new ArgReader(args);
        string certPath = opts.Require("--cert", "Certificate file");
        string keyPath = opts.Require("--key", "Key file");

        using var certContent = ContentLoader.LoadFile(certPath, opts.Get("--password"));
        using var keyContent = ContentLoader.LoadFile(keyPath, opts.Get("--key-password"));
        if (certContent.Certificates.Count == 0)
            throw new CertConvertException("No certificate found in the certificate file.");
        if (keyContent.PrivateKeys.Count == 0)
            throw new CertConvertException("No private key found in the key file.");

        bool match = KeyTools.Matches(
            certContent.Certificates[0], keyContent.PrivateKeys[0].Key);
        Console.WriteLine(match
            ? "MATCH: the private key belongs to the certificate."
            : "NO MATCH: the private key does not belong to the certificate.");
        return match ? CliRunner.ExitOk : CliRunner.ExitFailure;
    }

    // ---------- gen ----------

    public static int GenKey(string[] args)
    {
        var opts = new ArgReader(args);
        string outPath = opts.Require("--out", "Output file");
        using var entry = Generator.CreateKey(
            ParseAlgorithm(opts.Get("--algorithm") ?? "rsa2048"));

        string? password = opts.Get("--out-password");
        string pem = password is null
            ? KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8Pem)
            : KeyTools.ExportPem(entry.Key, KeyOutputFormat.Pkcs8EncryptedPem, password);
        WriteFile(outPath, Encoding.ASCII.GetBytes(pem));
        Console.WriteLine($"Generated {entry.Description} key.");
        return CliRunner.ExitOk;
    }

    public static int GenCsr(string[] args)
    {
        var opts = new ArgReader(args);
        string outPath = opts.Require("--out", "Output file");
        string keyPath = opts.Require("--key", "Key file");

        using var keyContent = ContentLoader.LoadFile(keyPath, opts.Get("--key-password"));
        if (keyContent.PrivateKeys.Count == 0)
            throw new CertConvertException("No private key found in the key file.");

        string csr = Generator.CreateCsrPem(keyContent.PrivateKeys[0].Key, ReadSpec(opts));
        WriteFile(outPath, Encoding.ASCII.GetBytes(csr));
        return CliRunner.ExitOk;
    }

    public static int GenSelfSigned(string[] args)
    {
        var opts = new ArgReader(args, "--ca");
        string outPath = opts.Require("--out", "Output file");

        PrivateKeyEntry keyEntry;
        LoadedContent? keyContent = null;
        if (opts.Get("--key") is { } keyPath)
        {
            keyContent = ContentLoader.LoadFile(keyPath, opts.Get("--key-password"));
            if (keyContent.PrivateKeys.Count == 0)
                throw new CertConvertException("No private key found in the key file.");
            keyEntry = keyContent.PrivateKeys[0];
        }
        else if (opts.Get("--new-key") is { } alg)
        {
            string keyOut = opts.Require("--key-out", "Key output file (--key-out)");
            keyEntry = Generator.CreateKey(ParseAlgorithm(alg));
            string? kp = opts.Get("--out-password");
            string pem = kp is null
                ? KeyTools.ExportPem(keyEntry.Key, KeyOutputFormat.Pkcs8Pem)
                : KeyTools.ExportPem(keyEntry.Key, KeyOutputFormat.Pkcs8EncryptedPem, kp);
            WriteFile(keyOut, Encoding.ASCII.GetBytes(pem));
        }
        else
        {
            throw new CertConvertException(
                "Supply --key <file>, or --new-key <algorithm> with --key-out <file>.");
        }

        try
        {
            var spec = ReadSpec(opts) with
            {
                ValidityDays = int.TryParse(opts.Get("--days") ?? "365", out int d)
                    ? d
                    : throw new CertConvertException("--days must be a number."),
                IsCertificateAuthority = opts.Has("--ca"),
            };
            using var cert = Generator.CreateSelfSigned(keyEntry.Key, spec);
            WriteFile(outPath, Encoding.ASCII.GetBytes(cert.ExportCertificatePem() + "\n"));
            Console.WriteLine($"Generated self-signed certificate: {Inspector.Inspect(cert).DisplayName}");
        }
        finally
        {
            if (keyContent is not null) keyContent.Dispose();
            else keyEntry.Dispose();
        }
        return CliRunner.ExitOk;
    }

    // ---------- update ----------

    public static int Update(string[] args)
    {
        var opts = new ArgReader(args, "--install", "--check");
        var service = new Services.UpdateService();

        var result = service.CheckAsync().GetAwaiter().GetResult();
        switch (result.Status)
        {
            case Services.UpdateStatus.CheckFailed:
                Console.Error.WriteLine($"Update check failed: {result.Message}");
                return CliRunner.ExitFailure;
            case Services.UpdateStatus.UpToDate:
                Console.WriteLine($"Up to date — {result.CurrentVersion} is the latest version.");
                return CliRunner.ExitOk;
        }

        Console.WriteLine(
            $"Update available: {result.LatestVersion} (you have {result.CurrentVersion}).");
        if (result.ReleaseUrl is { Length: > 0 })
            Console.WriteLine($"Release notes: {result.ReleaseUrl}");
        if (!opts.Has("--install"))
        {
            Console.WriteLine("Run \"certconvert update --install\" to download and apply it.");
            return CliRunner.ExitOk;
        }
        if (result.AssetUrl is null || result.AssetName is null)
        {
            Console.Error.WriteLine("No build for this platform is attached to the release.");
            return CliRunner.ExitFailure;
        }

        Console.WriteLine($"Downloading {result.AssetName}…");
        int lastReported = -10;
        var progress = new Progress<double>(p =>
        {
            int pct = (int)(p * 100);
            if (pct >= lastReported + 10)
            {
                lastReported = pct;
                Console.WriteLine($"  {pct}%");
            }
        });
        string zip = service.DownloadAsync(result.AssetUrl, result.AssetName, progress)
            .GetAwaiter().GetResult();

        bool? verified = service
            .VerifyChecksumAsync(zip, result.ChecksumsUrl, result.AssetName)
            .GetAwaiter().GetResult();
        if (verified == false)
        {
            Console.Error.WriteLine("Checksum verification FAILED — update aborted.");
            return CliRunner.ExitFailure;
        }
        Console.WriteLine(verified is null
            ? "This release publishes no checksum; applying the download as-is (TLS-protected)."
            : "Checksum verified.");

        var apply = service.ApplyAsync(zip).GetAwaiter().GetResult();
        Console.WriteLine(apply.Message);
        return apply.Ok ? CliRunner.ExitOk : CliRunner.ExitFailure;
    }

    // ---------- shared helpers ----------

    private static (List<X509Certificate2> Certs, List<PrivateKeyEntry> Keys) LoadInputs(
        ArgReader opts)
    {
        var certs = new List<X509Certificate2>();
        var keys = new List<PrivateKeyEntry>();
        foreach (var path in opts.Positionals)
        {
            var content = ContentLoader.LoadFile(path, opts.Get("--password"));
            certs.AddRange(content.Certificates);
            keys.AddRange(content.PrivateKeys);
        }
        if (opts.Get("--key") is { } keyPath)
        {
            var keyContent = ContentLoader.LoadFile(
                keyPath, opts.Get("--key-password") ?? opts.Get("--password"));
            if (keyContent.PrivateKeys.Count == 0)
                throw new CertConvertException($"No private key found in {keyPath}.");
            keys.AddRange(keyContent.PrivateKeys);
            certs.AddRange(keyContent.Certificates);
        }
        Converter.RemoveDuplicates(certs);
        return (certs, keys);
    }

    private static void ExportCertificates(
        IReadOnlyList<X509Certificate2> certs,
        List<PrivateKeyEntry> keys,
        string outPath,
        CertOutputFormat format,
        ArgReader opts)
    {
        AsymmetricAlgorithm? pfxKey = null;
        if (format == CertOutputFormat.Pkcs12 && keys.Count > 0)
        {
            pfxKey = keys
                .FirstOrDefault(k => certs.Any(c => KeyTools.Matches(c, k.Key)))?.Key
                ?? throw new CertConvertException(
                    "None of the supplied private keys match a certificate being exported.");
            if (keys.Count > 1)
                Console.Error.WriteLine(
                    $"Warning: {keys.Count - 1} unused key(s) were ignored — a PFX carries one key.");
        }
        if (format == CertOutputFormat.Pkcs12 && pfxKey is null)
            Console.Error.WriteLine("Warning: writing a PKCS #12 file without a private key.");
        if (format == CertOutputFormat.Pkcs12 && string.IsNullOrEmpty(opts.Get("--out-password")))
            Console.Error.WriteLine("Warning: the PKCS #12 file will have an empty password.");

        byte[] bytes = Converter.Export(certs, format, new ExportOptions
        {
            Password = opts.Get("--out-password"),
            PrivateKey = pfxKey,
            Encryption = opts.Has("--legacy") ? Pkcs12Encryption.Legacy : Pkcs12Encryption.Modern,
        });
        WriteFile(outPath, bytes);
    }

    private static CertOutputFormat ResolveFormat(string? to, string outPath)
    {
        if (to is null)
            return Converter.GuessFormatFromExtension(outPath)
                ?? throw new CertConvertException(
                    $"Cannot guess the format from \"{Path.GetExtension(outPath)}\" — pass --to.");
        return to.ToLowerInvariant() switch
        {
            "pem" => CertOutputFormat.Pem,
            "der" or "cer" => CertOutputFormat.Der,
            "p7b" or "pkcs7" => CertOutputFormat.Pkcs7Der,
            "p7b-pem" or "pkcs7-pem" => CertOutputFormat.Pkcs7Pem,
            "pfx" or "p12" or "pkcs12" => CertOutputFormat.Pkcs12,
            _ => throw new CertConvertException(
                $"Unknown format \"{to}\". Use pem, der, p7b, p7b-pem or pfx."),
        };
    }

    private static KeyAlgorithmChoice ParseAlgorithm(string name) => name.ToLowerInvariant() switch
    {
        "rsa2048" => KeyAlgorithmChoice.Rsa2048,
        "rsa3072" => KeyAlgorithmChoice.Rsa3072,
        "rsa4096" => KeyAlgorithmChoice.Rsa4096,
        "p256" or "ecp256" => KeyAlgorithmChoice.EcP256,
        "p384" or "ecp384" => KeyAlgorithmChoice.EcP384,
        "p521" or "ecp521" => KeyAlgorithmChoice.EcP521,
        _ => throw new CertConvertException(
            $"Unknown algorithm \"{name}\". Use rsa2048, rsa3072, rsa4096, p256, p384 or p521."),
    };

    private static CertSpec ReadSpec(ArgReader opts) => new()
    {
        CommonName = opts.Require("--cn", "Common name"),
        Organization = opts.Get("--org"),
        OrganizationalUnit = opts.Get("--ou"),
        Country = opts.Get("--country"),
        State = opts.Get("--state"),
        Locality = opts.Get("--locality"),
        DnsNames = SplitList(opts.Get("--dns")),
        IpAddresses = SplitList(opts.Get("--ip")),
    };

    private static string[] SplitList(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];

    private static void WriteFile(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"Wrote {path} ({bytes.Length:N0} bytes).");
    }

    private static void DisposeAll(List<X509Certificate2> certs, List<PrivateKeyEntry> keys)
    {
        foreach (var c in certs) c.Dispose();
        foreach (var k in keys) k.Dispose();
    }
}

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
            return CliRunner.Usage("Usage: certconvert inspect <file> [--password <pw>] [--hostname <host>]");

        string? host = opts.Get("--hostname");
        bool hostCovered = false;
        using var content = ContentLoader.LoadFile(opts.Positionals[0], opts.Password("--password"));
        Console.WriteLine($"Detected: {content.SourceDescription}");
        Console.WriteLine();

        for (int i = 0; i < content.Certificates.Count; i++)
        {
            if (content.Certificates.Count > 1)
                Console.WriteLine($"--- Certificate {i + 1} of {content.Certificates.Count} ---");
            var info = Inspector.Inspect(content.Certificates[i]);
            Console.Write(Inspector.ToText(info));
            Console.Write(HealthCheck.ToText(HealthCheck.Analyse(info)));
            if (host is not null)
            {
                var r = HostnameCheck.Check(content.Certificates[i], host);
                Console.WriteLine($"Host {host}:   {r.Explanation}");
                hostCovered |= r.IsCovered;
            }
            Console.WriteLine();
        }
        foreach (var key in content.PrivateKeys)
            Console.WriteLine($"Private key:   {key.Description}");

        for (int i = 0; i < content.CertificateRequests.Count; i++)
        {
            if (content.CertificateRequests.Count > 1)
                Console.WriteLine($"--- Certificate request {i + 1} of {content.CertificateRequests.Count} ---");
            var csr = CsrInspector.InspectPem(content.CertificateRequests[i]);
            Console.Write(CsrInspector.ToText(csr));
            Console.Write(HealthCheck.ToText(HealthCheck.Analyse(csr)));
            Console.WriteLine();
        }
        // A host-name check that finds no certificate to test against is a failure,
        // not a silent pass — say so and exit non-zero.
        if (host is not null && content.Certificates.Count == 0)
            Console.WriteLine($"Host {host}:   no certificate in this file to check it against.");
        // Adding --hostname can't change any call that omits it.
        return (host is not null && !hostCovered) ? CliRunner.ExitFailure : CliRunner.ExitOk;
    }

    // ---------- check ----------

    public static int Check(string[] args)
    {
        var opts = new ArgReader(args);
        if (opts.Positionals.Count != 1)
            return CliRunner.Usage("Usage: certconvert check <file> [--password <pw>]");

        using var content = ContentLoader.LoadFile(opts.Positionals[0], opts.Password("--password"));
        if (content.Certificates.Count == 0 && content.CertificateRequests.Count == 0)
            throw new CertConvertException("No certificate or certificate request found to check.");

        bool anyCritical = false;
        foreach (var cert in content.Certificates)
        {
            var info = Inspector.Inspect(cert);
            var findings = HealthCheck.Analyse(info);
            Console.WriteLine($"{info.DisplayName}:");
            Console.Write(HealthCheck.ToText(findings));
            Console.WriteLine();
            anyCritical |= findings.Any(f => f.Severity == Severity.Critical);
        }
        foreach (var csrPem in content.CertificateRequests)
        {
            var csr = CsrInspector.InspectPem(csrPem);
            var findings = HealthCheck.Analyse(csr);
            Console.WriteLine($"{csr.DisplayName} (request):");
            Console.Write(HealthCheck.ToText(findings));
            Console.WriteLine();
            anyCritical |= findings.Any(f => f.Severity == Severity.Critical);
        }
        return anyCritical ? CliRunner.ExitFailure : CliRunner.ExitOk;
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

        using var content = ContentLoader.LoadFile(opts.Positionals[0], opts.Password("--password"));
        if (content.PrivateKeys.Count == 0)
            throw new CertConvertException("No private key found in the input.");
        var key = content.PrivateKeys[0].Key;

        string to = opts.Get("--to")?.ToLowerInvariant()
            ?? (Path.GetExtension(outPath).ToLowerInvariant() == ".der" ? "der" : "pkcs8");
        // Only encrypted output takes a password. Resolve it there and nowhere else,
        // so no one is asked to type a password that would then be thrown away.
        if (to != "pkcs8-enc")
            opts.WarnIfUnused("--out-password",
                $"the output format {to} is not encrypted; use --to pkcs8-enc to encrypt the key");

        byte[] bytes = to switch
        {
            "pkcs8" => Encoding.ASCII.GetBytes(KeyTools.ExportPem(key, KeyOutputFormat.Pkcs8Pem)),
            "pkcs8-enc" => Encoding.ASCII.GetBytes(KeyTools.ExportPem(
                key, KeyOutputFormat.Pkcs8EncryptedPem,
                opts.Password("--out-password") ?? throw new CertConvertException(
                    "A new password is required for encrypted output. Give one with " +
                    PasswordSources.SaferForms("--out-password") + "."))),
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

        using var certContent = ContentLoader.LoadFile(certPath, opts.Password("--password"));
        using var keyContent = ContentLoader.LoadFile(keyPath, opts.Password("--key-password"));
        if (keyContent.PrivateKeys.Count == 0)
            throw new CertConvertException("No private key found in the key file.");
        var key = keyContent.PrivateKeys[0].Key;

        // Check every item in the file — certificates AND requests, not one or the
        // other. A bundle may hold both, and the key matches at most one.
        var certs = certContent.Certificates;
        var reqs = certContent.CertificateRequests;
        if (certs.Count == 0 && reqs.Count == 0)
            throw new CertConvertException(
                "No certificate or certificate request found in the certificate file.");

        bool match = certs.Any(c => KeyTools.Matches(c, key))
            || KeyTools.MatchesAnyRequest(reqs, key);
        string against = certs.Count > 0 && reqs.Count > 0 ? "certificate or request in the file"
            : certs.Count > 0 ? (certs.Count > 1 ? "any certificate in the file" : "certificate")
            : reqs.Count > 1 ? "any request in the file" : "certificate request";

        Console.WriteLine(match
            ? $"MATCH: the private key belongs to the {against}."
            : $"NO MATCH: the private key does not belong to the {against}.");
        return match ? CliRunner.ExitOk : CliRunner.ExitFailure;
    }

    // ---------- gen ----------

    public static int GenKey(string[] args)
    {
        var opts = new ArgReader(args);
        string outPath = opts.Require("--out", "Output file");
        using var entry = Generator.CreateKey(
            ParseAlgorithm(opts.Get("--algorithm") ?? "rsa2048"));

        string? password = opts.Password("--out-password");
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

        using var keyContent = ContentLoader.LoadFile(keyPath, opts.Password("--key-password"));
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

        PrivateKeyEntry? keyEntry = null;
        LoadedContent? keyContent = null;
        try
        {
            // Acquire the key inside the try so a failed --key-out write can't
            // leak a freshly generated private key.
            if (opts.Get("--key") is { } keyPath)
            {
                keyContent = ContentLoader.LoadFile(keyPath, opts.Password("--key-password"));
                if (keyContent.PrivateKeys.Count == 0)
                    throw new CertConvertException("No private key found in the key file.");
                keyEntry = keyContent.PrivateKeys[0];
            }
            else if (opts.Get("--new-key") is { } alg)
            {
                string keyOut = opts.Require("--key-out", "Key output file (--key-out)");
                keyEntry = Generator.CreateKey(ParseAlgorithm(alg));
                string? kp = opts.Password("--out-password");
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
            else keyEntry?.Dispose();
        }
        return CliRunner.ExitOk;
    }

    // ---------- update ----------

    public static int Update(string[] args)
    {
        var opts = new ArgReader(args, "--install");
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

        var verified = service
            .VerifyChecksumAsync(zip, result.ChecksumsUrl, result.AssetName)
            .GetAwaiter().GetResult();
        if (verified == Services.ChecksumResult.Failed)
        {
            Console.Error.WriteLine("Checksum verification FAILED — update aborted.");
            return CliRunner.ExitFailure;
        }
        Console.WriteLine(verified == Services.ChecksumResult.NoChecksumFile
            ? "This release publishes no checksum; applying the download as-is (TLS-protected)."
            : "Checksum verified.");

        var apply = service.ApplyAsync(zip).GetAwaiter().GetResult();
        Console.WriteLine(apply.Message);
        return apply.Ok ? CliRunner.ExitOk : CliRunner.ExitFailure;
    }

    // ---------- ca sign ----------

    public static int CaSign(string[] args)
    {
        var opts = new ArgReader(args, "--chain", "--force");
        string csrPath = opts.Require("--csr", "Certificate request file (--csr)");
        string caCertPath = opts.Require("--ca-cert", "CA certificate file (--ca-cert)");
        string outPath = opts.Require("--out", "Output file");

        // CSR (PEM or DER) -> DER bytes for the signer.
        using var csrContent = ContentLoader.LoadFile(csrPath);
        if (csrContent.CertificateRequestPem is not { } csrPem)
            throw new CertConvertException(
                $"No certificate request found in {Path.GetFileName(csrPath)}.");
        var csrBlocks = PemUtil.ParseAll(csrPem);
        if (csrBlocks.Count == 0)
            throw new CertConvertException($"No certificate request found in {Path.GetFileName(csrPath)}.");
        byte[] csrDer = csrBlocks[0].Der;

        // CA cert (may be a multi-cert PEM: leaf CA -> root) + its key (from a PFX, or --ca-key).
        string? pw = opts.Password("--ca-key-password");
        using var caCertContent = ContentLoader.LoadFile(caCertPath, pw);
        if (caCertContent.Certificates.Count == 0)
            throw new CertConvertException("No certificate found in the CA certificate file.");
        LoadedContent? caKeyContent = null;
        try
        {
            AsymmetricAlgorithm caKey;
            if (caCertContent.PrivateKeys.Count > 0)
            {
                caKey = caCertContent.PrivateKeys[0].Key;          // PFX supplied both
            }
            else
            {
                string caKeyPath = opts.Require("--ca-key", "CA private key file (--ca-key)");
                caKeyContent = ContentLoader.LoadFile(caKeyPath, pw);
                if (caKeyContent.PrivateKeys.Count == 0)
                    throw new CertConvertException("No private key found in the CA key file.");
                caKey = caKeyContent.PrivateKeys[0].Key;
            }

            // A multi-cert CA file (issuing CA + root) may not list the signing cert first;
            // pick the one the key belongs to. Fall back to the first — CaSigner re-checks
            // the match and fails with a clear error if none fits.
            var caCert = caCertContent.Certificates.FirstOrDefault(c => KeyTools.Matches(c, caKey))
                ?? caCertContent.Certificates[0];

            var issuance = new IssuanceOptions
            {
                ValidityDays = int.TryParse(opts.Get("--days") ?? "365", out int d) ? d
                    : throw new CertConvertException("--days must be a number."),
                Role = ParseRole(opts.Get("--type")),
                DnsNames = SplitList(opts.Get("--dns")),
                IpAddresses = SplitList(opts.Get("--ip")),
                EmailNames = SplitList(opts.Get("--email")),
                Uris = SplitList(opts.Get("--uri")),
                AllowNonCaIssuer = opts.Has("--force"),
            };

            using var issued = CaSigner.Sign(caCert, caKey, csrDer, issuance);

            // Output is cert-only (PEM cert, or issued+CA chain as PEM/P7B). No PFX: there
            // is no leaf private key. The CA private key is never written.
            // Leaf-to-root order: issued cert, then the matched CA, then any remaining
            // certificates from the CA file (a multi-cert bundle may list them in any order).
            var certs = new List<X509Certificate2> { issued };
            if (opts.Has("--chain"))
            {
                certs.Add(caCert);
                foreach (var c in caCertContent.Certificates)
                    if (!ReferenceEquals(c, caCert)) certs.Add(c);
            }
            byte[] bytes = Converter.Export(certs, ResolveCaFormat(opts.Get("--to"), outPath));
            WriteFile(outPath, bytes);

            var info = Inspector.Inspect(issued);
            Console.WriteLine($"Issued {info.DisplayName}, valid until {info.NotAfter:yyyy-MM-dd} UTC.");
            foreach (var f in HealthCheck.Analyse(info))
                if (f.Severity != Severity.Notice)
                    Console.Error.WriteLine($"  [{f.Severity.ToString().ToUpperInvariant()}] {f.Title}");
            return CliRunner.ExitOk;
        }
        finally
        {
            caKeyContent?.Dispose();
        }
    }

    private static IssuedRole ParseRole(string? type) => (type ?? "server").ToLowerInvariant() switch
    {
        "server" => IssuedRole.TlsServer,
        "client" => IssuedRole.TlsClient,
        _ => throw new CertConvertException($"Unknown --type \"{type}\". Use server or client."),
    };

    private static CertOutputFormat ResolveCaFormat(string? to, string outPath)
    {
        string t = (to ?? Path.GetExtension(outPath).TrimStart('.')).ToLowerInvariant();
        return t switch
        {
            "pem" or "crt" or "cer" => CertOutputFormat.Pem,
            "p7b" or "pkcs7" => CertOutputFormat.Pkcs7Der,
            "pfx" or "p12" or "pkcs12" => throw new CertConvertException(
                "A signed certificate has no private key to place in a PFX — use --to pem or p7b."),
            "" => throw new CertConvertException("Cannot guess the output format — pass --to pem or p7b."),
            _ => throw new CertConvertException($"Unknown format \"{t}\". Use pem or p7b."),
        };
    }

    // ---------- shared helpers ----------

    private static (List<X509Certificate2> Certs, List<PrivateKeyEntry> Keys) LoadInputs(
        ArgReader opts)
    {
        var certs = new List<X509Certificate2>();
        var keys = new List<PrivateKeyEntry>();
        foreach (var path in opts.Positionals)
        {
            var content = ContentLoader.LoadFile(path, opts.Password("--password"));
            certs.AddRange(content.Certificates);
            keys.AddRange(content.PrivateKeys);
        }
        if (opts.Get("--key") is { } keyPath)
        {
            var keyContent = ContentLoader.LoadFile(
                keyPath, opts.Password("--key-password") ?? opts.Password("--password"));
            if (keyContent.PrivateKeys.Count == 0)
                throw new CertConvertException($"No private key found in {keyPath}.");
            keys.AddRange(keyContent.PrivateKeys);
            certs.AddRange(keyContent.Certificates);
        }
        else
        {
            opts.WarnIfUnused("--key-password", "no --key file was given");
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
        // Only a PFX is password-protected; the other formats hold certificates alone.
        string? outPassword = null;
        if (format == CertOutputFormat.Pkcs12)
        {
            outPassword = opts.Password("--out-password");
            if (string.IsNullOrEmpty(outPassword))
                Console.Error.WriteLine("Warning: the PKCS #12 file will have an empty password.");
        }
        else
        {
            opts.WarnIfUnused("--out-password", "only a PFX output takes a password");
        }

        byte[] bytes = Converter.Export(certs, format, new ExportOptions
        {
            Password = outPassword,
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

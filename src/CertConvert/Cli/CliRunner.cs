using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CertConvert.Core;

namespace CertConvert.Cli;

/// <summary>
/// Entry point for command-line use. The same binary opens the GUI when run without
/// arguments; with arguments it behaves as a scriptable openssl replacement.
/// </summary>
public static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitFailure = 2;

    public static int Run(string[] args)
    {
        AttachToParentConsoleOnWindows();
        try
        {
            return Dispatch(args);
        }
        catch (PasswordRequiredException e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            Console.Error.WriteLine("Pass it with --password (or --key-password for a separate key file).");
            return ExitFailure;
        }
        catch (CertConvertException e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return ExitFailure;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Unexpected error: {e.Message}");
            return ExitFailure;
        }
    }

    private static int Dispatch(string[] args) => args[0].ToLowerInvariant() switch
    {
        "-h" or "--help" or "help" => PrintHelp(),
        "-v" or "--version" or "version" => PrintVersion(),
        "inspect" => Commands.Inspect(args[1..]),
        "convert" => Commands.Convert(args[1..]),
        "chain" => DispatchChain(args[1..]),
        "key" => DispatchKey(args[1..]),
        "gen" => DispatchGen(args[1..]),
        "update" => Commands.Update(args[1..]),
        _ => Usage($"Unknown command \"{args[0]}\"."),
    };

    private static int DispatchChain(string[] rest) => rest.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "build" => Commands.ChainBuild(rest[1..]),
        "verify" => Commands.ChainVerify(rest[1..]),
        _ => Usage("Usage: certconvert chain <build|verify> ..."),
    };

    private static int DispatchKey(string[] rest) => rest.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "convert" => Commands.KeyConvert(rest[1..]),
        "match" => Commands.KeyMatch(rest[1..]),
        _ => Usage("Usage: certconvert key <convert|match> ..."),
    };

    private static int DispatchGen(string[] rest) => rest.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "key" => Commands.GenKey(rest[1..]),
        "csr" => Commands.GenCsr(rest[1..]),
        "selfsigned" => Commands.GenSelfSigned(rest[1..]),
        _ => Usage("Usage: certconvert gen <key|csr|selfsigned> ..."),
    };

    internal static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run \"certconvert --help\" for usage.");
        return ExitUsage;
    }

    private static int PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        Console.WriteLine($"certconvert {version}");
        return ExitOk;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            certconvert — offline certificate conversion, chaining and inspection.
            Run without arguments to open the GUI.

            Usage:
              certconvert inspect <file> [--password <pw>]
                  Decode and print certificates, keys or CSRs in any supported format.

              certconvert convert <in>... -o <out> [--to pem|der|p7b|p7b-pem|pfx]
                                  [--password <pw>] [--key <file>] [--key-password <pw>]
                                  [--out-password <pw>] [--legacy]
                  Convert between PEM, DER, PKCS #7 and PKCS #12. The target format is
                  taken from --to or guessed from the output extension. --key adds a
                  private key for PFX output; --legacy uses 3DES/SHA-1 for old importers.

              certconvert chain build <files>... -o <out> [--to ...] [same options]
                  Order certificates leaf → intermediate → root and export the chain.

              certconvert chain verify <files>... [--password <pw>] [--system-roots]
                  Validate order, signatures, validity windows and CA flags offline.
                  --system-roots also trusts the operating system root store.

              certconvert key convert <in> -o <out> [--to pkcs8|pkcs8-enc|pkcs1|sec1|der]
                                  [--password <pw>] [--out-password <pw>]
                  Re-encode a private key. pkcs8-enc requires --out-password.

              certconvert key match --cert <file> --key <file>
                                  [--password <pw>] [--key-password <pw>]
                  Check whether a private key belongs to a certificate.

              certconvert gen key [--algorithm rsa2048|rsa3072|rsa4096|p256|p384|p521]
                                  -o <out> [--out-password <pw>]

              certconvert gen csr --key <file> --cn <name> -o <out>
                                  [--org|--ou|--country|--state|--locality <value>]
                                  [--dns a,b] [--ip x,y] [--key-password <pw>]

              certconvert gen selfsigned (--key <file> | --new-key <alg> --key-out <file>)
                                  --cn <name> -o <out> [--days <n>] [--ca] [same subject options]

              certconvert update [--install]
                  Check GitHub for a newer release; --install downloads, verifies and
                  applies it. This is the only command that uses the network.

            Exit codes: 0 success · 1 usage error · 2 failure (including an invalid chain
            or a key that does not match).

            Everything else runs locally; certificate data never leaves this machine.
            """);
        return ExitOk;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// The Windows build is a GUI-subsystem executable, so console output must be
    /// explicitly attached to the invoking terminal. No-op elsewhere.
    /// </summary>
    private static void AttachToParentConsoleOnWindows()
    {
        if (OperatingSystem.IsWindows())
            AttachConsole(-1); // ATTACH_PARENT_PROCESS; fails harmlessly when double-clicked
    }
}

/// <summary>Tiny option parser: positionals plus --name value / --flag tokens.</summary>
internal sealed class ArgReader
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = new();

    public ArgReader(string[] args, params string[] boolFlags)
    {
        var flagSet = new HashSet<string>(boolFlags, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o") a = "--out";
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (flagSet.Contains(a))
                {
                    _flags.Add(a);
                }
                else
                {
                    if (i + 1 >= args.Length)
                        throw new CertConvertException($"Option {a} needs a value.");
                    _values[a] = args[++i];
                }
            }
            else
            {
                Positionals.Add(a);
            }
        }
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public bool Has(string flag) => _flags.Contains(flag);

    public string Require(string name, string hint)
    {
        return Get(name) ?? throw new CertConvertException($"{hint} ({name}) is required.");
    }
}

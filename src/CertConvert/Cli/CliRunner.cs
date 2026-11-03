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
            // SaferForms leaves out the prompt on Windows, where it is not offered.
            Console.Error.WriteLine(
                $"Supply it with {PasswordSources.SaferForms("--password")}. " +
                "A separate key file uses the --key-password forms.");
            return ExitFailure;
        }
        catch (UsageException e)
        {
            return Usage($"Error: {e.Message}");
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

    private static int Dispatch(string[] args)
    {
        string verb = args[0].ToLowerInvariant();

        // --version and --help always work, in every variant.
        if (verb is "-h" or "--help" or "help")
            return PrintHelp();
        if (verb is "-v" or "--version" or "version")
            return PrintVersion();

#if STORE_BUILD
        // A sandboxed store process cannot open arbitrary command-line paths, and
        // updates come from the store — intercept every remaining verb centrally.
        if (verb == "update")
        {
            Console.WriteLine("Updates are delivered by the store.");
            return ExitOk;
        }
        Console.Error.WriteLine(AppInfo.StoreCliPointer);
        return ExitFailure;
#else
        return verb switch
        {
            "inspect" => Commands.Inspect(args[1..]),
            "check" => Commands.Check(args[1..]),
            "convert" => Commands.Convert(args[1..]),
            "chain" => DispatchChain(args[1..]),
            "key" => DispatchKey(args[1..]),
            "gen" => DispatchGen(args[1..]),
            "ca" => DispatchCa(args[1..]),
            "update" => Commands.Update(args[1..]),
            _ => Usage($"Unknown command \"{args[0]}\"."),
        };
#endif
    }

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

    private static int DispatchCa(string[] rest) => rest.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "sign" => Commands.CaSign(rest[1..]),
        _ => Usage("Usage: certconvert ca sign --csr <f> --ca-cert <f> [--ca-key <f>] -o <out> ..."),
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
#if STORE_BUILD
        // Every file verb is refused in this variant (see Dispatch), so printing the
        // full command reference would be advertising something that cannot run.
        Console.WriteLine("""
            certconvert — offline certificate conversion, chaining and inspection.
            Run without arguments to open the app.
            """);
        Console.WriteLine();
        Console.WriteLine(AppInfo.StoreCliPointer);
        return ExitOk;
#else
        Console.WriteLine("""
            certconvert — offline certificate conversion, chaining and inspection.
            Run without arguments to open the GUI.

            Usage:
              certconvert inspect <file> [--password <pw>] [--hostname <host>]
                  Decode and print certificates, keys or CSRs in any supported format,
                  each followed by a plain-English health check. --hostname also reports
                  whether each certificate covers that host (exit 2 if none do).

              certconvert check <file> [--password <pw>]
                  Run only the health check. Exits 2 if anything Critical is found,
                  so it can gate a script or CI job.

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
                  Check whether a private key belongs to a certificate or CSR.

              certconvert gen key [--algorithm rsa2048|rsa3072|rsa4096|p256|p384|p521]
                                  -o <out> [--out-password <pw>]

              certconvert gen csr --key <file> --cn <name> -o <out>
                                  [--org|--ou|--country|--state|--locality <value>]
                                  [--dns a,b] [--ip x,y] [--key-password <pw>]

              certconvert gen selfsigned (--key <file> | --new-key <alg> --key-out <file>)
                                  --cn <name> -o <out> [--days <n>] [--ca] [same subject options]

              certconvert ca sign --csr <file> --ca-cert <file> [--ca-key <file>] -o <out>
                                  [--type server|client] [--days <n>] [--to pem|p7b]
                                  [--dns a,b] [--ip x,y] [--email a,b] [--uri a,b]
                                  [--chain] [--ca-key-password <pw>] [--force]
                  Sign a CSR with your own CA, issuing an end-entity certificate. The CA
                  key comes from a PFX --ca-cert or a separate --ca-key. Requested
                  extensions are never trusted: the issued cert is always end-entity,
                  usages are issuer-chosen, and only the SANs you pass are included.
                  The SUBJECT is taken verbatim from the CSR — inspect it first
                  (certconvert inspect <csr>) and verify it before you sign.
                  --chain also writes the CA. --force signs with a cert not marked as a CA.

              certconvert update [--install]
                  Check GitHub for a newer release; --install downloads, verifies and
                  applies it. This is the only command that uses the network.

            Passwords:
              Every password option above (--password, --key-password, --out-password,
              --ca-key-password) can be given four ways. Use one per password:
                --password-prompt        ask on the terminal without showing it
                                         (macOS and Linux; --out-password-prompt asks twice)
                --password-file <file>   the first line of a file; "-" reads it from
                                         standard input, e.g. piped from a password manager
                --password-env <NAME>    the value of an environment variable
                --password <value>       on the command line itself — avoid this: it is
                                         saved in shell history and visible to other processes

            Exit codes: 0 success · 1 usage error · 2 failure (including an invalid chain
            or a key that does not match).

            Everything else runs locally; certificate data never leaves this machine.
            """);
        return ExitOk;
#endif
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

/// <summary>
/// Tiny option parser: positionals plus --name value / --flag tokens. Password
/// options are handed to <see cref="PasswordSources"/>, which checks them as they
/// are parsed and reads each password only when a command asks for it.
/// </summary>
internal sealed class ArgReader
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly PasswordSources _passwords = new(PasswordIo.Current);
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
                else if (PasswordSources.IsPromptOption(a))
                {
                    _passwords.Add(a, "");
                }
                else
                {
                    // A misspelt password option must not be mistaken for an ordinary
                    // one: it would swallow the next argument and leave the password
                    // unset. Add() rejects any name it does not know.
                    bool isPassword = PasswordSources.LooksLikePasswordOption(a);
                    if (i + 1 >= args.Length)
                    {
                        if (!isPassword)   // unchanged behaviour for every existing option
                            throw new CertConvertException($"Option {a} needs a value.");
                        _passwords.Add(a, "");                   // unknown name -> clearer error
                        throw new UsageException($"Option {a} needs a value.");
                    }
                    if (isPassword) _passwords.Add(a, args[++i]);
                    else _values[a] = args[++i];
                }
            }
            else
            {
                Positionals.Add(a);
            }
        }
        _passwords.Validate();
    }

    public string? Get(string name)
    {
        // Passwords are never in _values; asking for one here would silently get null.
        if (PasswordSources.LooksLikePasswordOption(name))
            throw new InvalidOperationException($"Read {name} with Password(), not Get().");
        return _values.GetValueOrDefault(name);
    }
    public bool Has(string flag) => _flags.Contains(flag);

    /// <summary>
    /// The password for one of <see cref="PasswordSources.Names"/>, from whichever form
    /// was given (value, -file, -env or -prompt), or null when none was.
    /// </summary>
    public string? Password(string name) => _passwords.Resolve(name);

    /// <summary>
    /// Call when a command will not use a password the command line supplied: says so
    /// instead of quietly dropping it (or prompting for something then discarded).
    /// </summary>
    public void WarnIfUnused(string name, string because)
    {
        if (_passwords.OptionFor(name) is { } option)
            Console.Error.WriteLine($"Warning: {option} was ignored — {because}.");
    }

    public string Require(string name, string hint)
    {
        return Get(name) ?? throw new CertConvertException($"{hint} ({name}) is required.");
    }
}

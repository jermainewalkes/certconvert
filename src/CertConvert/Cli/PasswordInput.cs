using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CertConvert.Core;

namespace CertConvert.Cli;

/// <summary>
/// Everything the password options touch outside the process — environment, files,
/// standard input and the terminal — as one replaceable unit, so the resolution
/// rules in <see cref="PasswordSources"/> can be tested without a real console.
/// </summary>
internal sealed record PasswordIo
{
    public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Opens a password file as text: UTF-8, or whatever a byte-order mark declares.
    /// Throws <see cref="TerminalDeviceException"/> when the path turns out to be a
    /// terminal, by whatever name it was reached.
    /// </summary>
    public Func<string, TextReader> OpenFile { get; init; } = DefaultOpenFile;

    public Func<TextReader> Stdin { get; init; } = () => Console.In;

    /// <summary>True when standard input is a terminal rather than a pipe or file.</summary>
    public Func<bool> StdinIsTerminal { get; init; } = () => !Console.IsInputRedirected;

    public Func<bool> IsWindows { get; init; } = OperatingSystem.IsWindows;

    /// <summary>Shows a label and reads one line from the terminal without echoing it.</summary>
    public Func<string, string> ReadHidden { get; init; } = ConsolePrompt.ReadHidden;

    public Action<string> Warn { get; init; } = message => Console.Error.WriteLine(message);

    /// <summary>True when a password file can be read by users other than its owner.</summary>
    public Func<string, bool> IsReadableByOthers { get; init; } = DefaultIsReadableByOthers;

    public static PasswordIo Default { get; } = new();

    private static readonly AsyncLocal<PasswordIo?> Override = new();

    /// <summary>The I/O in effect: the real one, unless a test has substituted its own.</summary>
    public static PasswordIo Current => Override.Value ?? Default;

    /// <summary>Substitutes the I/O for the current async flow only (tests run in parallel).</summary>
    internal static IDisposable Use(PasswordIo io)
    {
        var previous = Override.Value;
        Override.Value = io;
        return new Restore(previous);
    }

    private sealed class Restore(PasswordIo? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }

    private static TextReader DefaultOpenFile(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Ask the system what was actually opened: /dev/tty, /dev/./tty, a symlink to
            // it and /proc/self/fd/0 on a keyboard are all the same echoing terminal.
            if (IsTerminal(stream))
                throw new TerminalDeviceException();
            // Strict decoding: bytes that are not valid text fail loudly instead of
            // quietly becoming a different password.
            return new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsTerminal(FileStream stream)
    {
        if (OperatingSystem.IsWindows()) return false;   // CON and CONIN$ are refused by name
        try
        {
            return isatty((int)stream.SafeFileHandle.DangerousGetHandle()) == 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;   // no C library to ask: the by-name checks still apply
        }
    }

    [DllImport("libc")]
    private static extern int isatty(int fd);

    private static bool DefaultIsReadableByOthers(string path)
    {
        if (OperatingSystem.IsWindows()) return false;   // ACLs, not mode bits
        try
        {
            string full = Path.GetFullPath(path);
            // Pipes and process substitutions (/dev/fd/N) have no meaningful mode.
            if (full.StartsWith("/dev/", StringComparison.Ordinal) ||
                full.StartsWith("/proc/", StringComparison.Ordinal))
                return false;
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            return (File.GetUnixFileMode(full) & others) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;   // the read itself reports a file that cannot be opened
        }
    }
}

/// <summary>
/// The command line itself is malformed — a misspelt password option, the same
/// password given twice, an option missing its value. Reported with exit code 1,
/// the documented "usage error", before anything is read or written.
/// </summary>
internal sealed class UsageException(string message) : CertConvertException(message);

/// <summary>A path given as a password file is really a terminal, which would echo.</summary>
internal sealed class TerminalDeviceException : Exception;

/// <summary>Reads a line from the terminal with echo off.</summary>
internal static class ConsolePrompt
{
    public static string ReadHidden(string label)
    {
        // The label goes to standard error so standard output stays clean for piping.
        Console.Error.Write(label);
        var typed = new StringBuilder();
        int deadReads = 0;
        while (true)
        {
            // intercept: true keeps the key off the screen. Ctrl+C is left to the
            // runtime, which ends the process and restores the terminal's settings.
            var key = Console.ReadKey(intercept: true);
            // A terminal that has gone away can hand back empty keys for ever. Give up
            // rather than spin.
            if (key.Key == 0 && key.KeyChar == '\0')
            {
                if (++deadReads > 256)
                    throw new CertConvertException("The terminal closed before a password was entered.");
                continue;
            }
            deadReads = 0;
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length == 0) continue;
                // Remove a whole character, which may be a surrogate pair.
                int cut = typed.Length >= 2 && char.IsLowSurrogate(typed[^1]) &&
                          char.IsHighSurrogate(typed[^2]) ? 2 : 1;
                typed.Length -= cut;
                continue;
            }
            if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                typed.Clear();   // Ctrl+U: start again, as a shell's own prompt allows
                continue;
            }
            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) continue;
            typed.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return typed.ToString();
    }
}

/// <summary>
/// Where each password comes from. Every password option <c>--X</c> can be given in
/// one of four ways: <c>--X value</c>, <c>--X-file path</c> (or <c>-</c> for standard
/// input), <c>--X-env NAME</c> or <c>--X-prompt</c>. The option names are checked
/// while the command line is parsed, before anything is read or written, so a
/// mistake fails early; the password itself is read only when a command asks for it.
/// </summary>
internal sealed class PasswordSources
{
    /// <summary>The password options the CLI understands.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["--password", "--key-password", "--out-password", "--ca-key-password"];

    /// <summary>Longest password accepted from a file or standard input.</summary>
    public const int MaxLength = 4096;

    private enum Kind { Literal, File, Env, Prompt }

    private readonly record struct Source(Kind Kind, string Option, string Value);

    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);
    private readonly PasswordIo _io;

    public PasswordSources(PasswordIo io) => _io = io;

    /// <summary>True for any option that names a password, whether or not it is valid.</summary>
    public static bool LooksLikePasswordOption(string option) =>
        option.Contains("passw", StringComparison.OrdinalIgnoreCase) ||       // --passwrd-file
        option.Contains("passphrase", StringComparison.OrdinalIgnoreCase);    // --key-passphrase

    /// <summary>True for the <c>--X-prompt</c> forms, which take no value.</summary>
    public static bool IsPromptOption(string option) =>
        TryParse(option, out _, out var kind) && kind == Kind.Prompt;

    private static bool TryParse(string option, out string name, out Kind kind)
    {
        string o = option.ToLowerInvariant();
        foreach (var n in Names)
        {
            (name, kind) = (n, Kind.Literal);
            if (o == n) return true;
            if (o == n + "-file") { kind = Kind.File; return true; }
            if (o == n + "-env") { kind = Kind.Env; return true; }
            if (o == n + "-prompt") { kind = Kind.Prompt; return true; }
        }
        (name, kind) = ("", Kind.Literal);
        return false;
    }

    /// <summary>
    /// Records one password option from the command line. Rejects a misspelt option,
    /// the same option twice and a second source for the same password.
    /// </summary>
    public void Add(string option, string value)
    {
        if (!TryParse(option, out var name, out var kind))
            throw new UsageException(
                $"Unknown option {option}. A password is given as {Names[0]}, {Names[1]}, " +
                $"{Names[2]} or {Names[3]}, each also available with -file, -env or -prompt " +
                "on the end.");

        if (_sources.TryGetValue(name, out var existing))
            throw new UsageException(
                string.Equals(existing.Option, option, StringComparison.OrdinalIgnoreCase)
                    ? $"Option {option} was given more than once."
                    : $"{existing.Option} and {option} both supply the same password. Use one of them.");

        if (kind is Kind.File or Kind.Env && value.Length == 0)
            throw new UsageException($"Option {option} needs a value.");

        _sources[name] = new Source(kind, option, value);
    }

    /// <summary>Checks the rules that span more than one password. Reads nothing.</summary>
    public void Validate()
    {
        var stdin = _sources.Values.Where(IsStdin).ToList();
        if (stdin.Count > 1)
            throw new UsageException(
                $"{stdin[0].Option} and {stdin[1].Option} both read standard input, which can " +
                "supply only one password.");

        var prompt = _sources.Values.FirstOrDefault(s => s.Kind == Kind.Prompt);
        if (stdin.Count == 1 && prompt.Option is not null)
            throw new UsageException(
                $"{stdin[0].Option} {stdin[0].Value} reads a piped password and {prompt.Option} needs the " +
                "keyboard, so they cannot be used together.");
    }

    /// <summary>
    /// The password for <paramref name="name"/>, or null when the command line gave no
    /// source for it. An empty password that was supplied on purpose is returned as "".
    /// Each password is read at most once.
    /// </summary>
    public string? Resolve(string name)
    {
        if (!_sources.TryGetValue(name, out var source)) return null;
        if (_resolved.TryGetValue(name, out var cached)) return cached;

        string value = source.Kind switch
        {
            Kind.Literal => source.Value,
            Kind.Env => FromEnv(source),
            Kind.File => IsStdin(source) ? FromStdin(source) : FromFile(source),
            _ => FromPrompt(name, source),
        };
        _resolved[name] = value;
        return value;
    }

    /// <summary>True when the command line gave a source for this password. Reads nothing.</summary>
    public bool Has(string name) => _sources.ContainsKey(name);

    /// <summary>The option that supplied this password, for messages. Null when none did.</summary>
    public string? OptionFor(string name) =>
        _sources.TryGetValue(name, out var source) ? source.Option : null;

    /// <summary>The safer forms of a password option that work on this system, for messages.</summary>
    public static string SaferForms(string name) => PasswordIo.Current.IsWindows()
        ? $"{name}-file or {name}-env"
        : $"{name}-prompt, {name}-file or {name}-env";

    /// <summary>
    /// "-" and the device names for standard input all mean the same pipe, so they share
    /// the rules: one password only, and never from a keyboard that would echo it.
    /// </summary>
    private static bool IsStdin(Source source) =>
        source.Kind == Kind.File && source.Value is "-" or "/dev/stdin" or "/dev/fd/0";

    private string FromEnv(Source source) =>
        _io.GetEnv(source.Value) ?? throw new CertConvertException(
            $"Environment variable {source.Value} (named by {source.Option}) is not set.");

    private string FromStdin(Source source)
    {
        if (_io.StdinIsTerminal())
            throw new CertConvertException(
                $"{source.Option} {source.Value} reads a piped password, but standard input is the keyboard " +
                "here and the password would be shown as you type. " + Alternatives(source));
        return ReadFirstLine(_io.Stdin(), "standard input", source)
            ?? throw new CertConvertException(
                $"No password arrived on standard input (for {source.Option} {source.Value}).");
    }

    private string FromFile(Source source)
    {
        // The terminal device echoes what is typed; the prompt form exists for that.
        if (IsConsoleName(source.Value))
            throw new CertConvertException(
                $"{source.Option} {source.Value} would read the keyboard and show the password " +
                "as you type. " + Alternatives(source));
        string shown = Path.GetFileName(source.Value);
        string? line;
        try
        {
            using var reader = _io.OpenFile(source.Value);
            line = ReadFirstLine(reader, $"the password file {shown}", source);
        }
        catch (TerminalDeviceException)
        {
            throw new CertConvertException(
                $"{source.Option} {source.Value} would read the keyboard and show the password " +
                "as you type. " + Alternatives(source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            // FileNotFound and DirectoryNotFound are IOExceptions. Report the reason
            // without a stack trace and without anything read from the file.
            string why = e is DecoderFallbackException ? "it is not valid text (expected UTF-8)"
                : Directory.Exists(source.Value) ? "it is a folder, not a file"
                : e is UnauthorizedAccessException ? "permission denied"
                : e is FileNotFoundException or DirectoryNotFoundException ? "it does not exist"
                : "it could not be read";
            throw new CertConvertException(
                $"Cannot read the password file {shown} (for {source.Option}): {why}.");
        }

        if (line is null)
            throw new CertConvertException(
                $"The password file {shown} (for {source.Option}) is empty. For an empty " +
                "password on purpose, put a single blank line in it.");

        if (_io.IsReadableByOthers(source.Value))
            // No ready-made command here: a file name is not safe to paste into a shell.
            _io.Warn($"Warning: the password file {shown} can be read by other users. " +
                     "Restrict it to your own account (mode 600).");
        return line;
    }

    /// <summary>
    /// The terminal by its well-known names: /dev/tty, and on Windows the console
    /// devices in any letter case, with or without the \\.\ device prefix or a colon.
    /// (On Unix every other alias is caught by asking the system once the file is open.)
    /// </summary>
    internal static bool IsConsoleName(string path)
    {
        if (path == "/dev/tty") return true;
        string name = path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[4..] : path;
        name = name.TrimEnd(':');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase);
    }

    private string FromPrompt(string name, Source source)
    {
        if (_io.IsWindows())
            throw new CertConvertException(
                $"{source.Option} is not available on Windows, where CertConvert is a windowed " +
                "program that shares the console with your shell, so typed keys cannot be read " +
                $"reliably. Use {name}-file or {name}-env.");
        if (!_io.StdinIsTerminal())
            throw new CertConvertException(
                $"{source.Option} needs the keyboard, but standard input is redirected. " +
                $"Use {name}-file or {name}-env.");

        string label = name switch
        {
            "--key-password" => "Password for the key file: ",
            "--out-password" => "New password for the output: ",
            "--ca-key-password" => "Password for the CA key: ",
            _ => "Password for the input: ",
        };
        string first = _io.ReadHidden(label);
        // A new password is typed blind, so ask twice: a slip would lock the output.
        if (name == "--out-password" && _io.ReadHidden("Repeat the new password: ") != first)
            throw new CertConvertException("The two passwords did not match. Nothing was written.");
        return first;
    }

    private string Alternatives(Source source)
    {
        string name = source.Option[..^"-file".Length];
        return _io.IsWindows()
            ? $"Pipe the password in, or use {name}-env."
            : $"Use {name}-prompt to type it unseen, or pipe it in.";
    }

    /// <summary>
    /// The first line of <paramref name="reader"/> without its line ending, or null when
    /// there is nothing at all to read. Only the line ending is removed: spaces are part
    /// of the password. Reads no further than it must.
    /// </summary>
    private static string? ReadFirstLine(TextReader reader, string what, Source source)
    {
        var line = new StringBuilder();
        bool sawAnything = false;
        while (true)
        {
            int c = reader.Read();
            if (c < 0) break;
            sawAnything = true;
            // LF, CRLF and a lone CR all end the line. After a CR we stop without looking
            // for an LF: peeking at a pipe can block, and nothing else reads this input.
            if (c is '\n' or '\r') break;
            if (line.Length == MaxLength)
                throw new CertConvertException(
                    $"The first line of {what} (for {source.Option}) is longer than " +
                    $"{MaxLength} characters, which is not a password.");
            line.Append((char)c);
        }
        return sawAnything ? line.ToString() : null;
    }
}

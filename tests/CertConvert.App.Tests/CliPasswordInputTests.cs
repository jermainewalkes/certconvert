using System.IO;
using CertConvert.Cli;
using CertConvert.Core;

namespace CertConvert.Gui.Tests;

/// <summary>
/// The CLI's password options: each password can come from the command line, a file,
/// standard input, an environment variable or a hidden prompt. These tests replace the
/// console, environment and terminal with fakes (per async flow, so they are safe to
/// run in parallel) and drive both the option parser and the real commands.
/// </summary>
public class CliPasswordInputTests
{
    private sealed class Fake
    {
        public Dictionary<string, string> Env { get; } = new();
        public string Stdin { get; set; } = "";
        public bool Terminal { get; set; } = true;
        public bool Windows { get; set; }
        public Queue<string> Typed { get; } = new();
        public List<string> Prompts { get; } = new();
        public List<string> Warnings { get; } = new();
        public HashSet<string> ReadableByOthers { get; } = new();
        public int StdinOpened { get; private set; }

        public IDisposable Install()
        {
            StringReader? reader = null;
            return PasswordIo.Use(PasswordIo.Default with
            {
                GetEnv = name => Env.GetValueOrDefault(name),
                Stdin = () => { StdinOpened++; return reader ??= new StringReader(Stdin); },
                StdinIsTerminal = () => Terminal,
                IsWindows = () => Windows,
                ReadHidden = label => { Prompts.Add(label); return Typed.Dequeue(); },
                Warn = Warnings.Add,
                IsReadableByOthers = ReadableByOthers.Contains,
            });
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string PasswordFile(string dir, string content, string name = "pw.txt")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Message(Action act) => Assert.ThrowsAny<CertConvertException>(act).Message;

    /// <summary>The message of a malformed command line, which is a usage error (exit 1).</summary>
    private static string UsageMessage(Action act) => Assert.Throws<UsageException>(act).Message;

    // ---------- the command line itself ----------

    [Fact]
    public void NoSource_IsNull_AndLiteralStillWorks()
    {
        using var _ = new Fake().Install();
        Assert.Null(new ArgReader(["in.pem"]).Password("--password"));
        Assert.Equal("s3cret", new ArgReader(["--password", "s3cret"]).Password("--password"));
        Assert.Equal("", new ArgReader(["--password", ""]).Password("--password"));   // empty on purpose
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--key-password")]
    [InlineData("--out-password")]
    [InlineData("--ca-key-password")]
    public void EveryPasswordOption_HasAllFourForms(string name)
    {
        var dir = TempDir();
        try
        {
            var fake = new Fake { Env = { ["PW"] = "from-env" } };
            fake.Typed.Enqueue("typed"); fake.Typed.Enqueue("typed");
            using var _ = fake.Install();
            Assert.Equal("lit", new ArgReader([name, "lit"]).Password(name));
            Assert.Equal("from-file", new ArgReader([name + "-file", PasswordFile(dir, "from-file\n")]).Password(name));
            Assert.Equal("from-env", new ArgReader([name + "-env", "PW"]).Password(name));
            Assert.Equal("typed", new ArgReader([name + "-prompt"]).Password(name));
            // Only a new password is asked for twice.
            Assert.Equal(name == "--out-password" ? 0 : 1, fake.Typed.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TwoSourcesForOnePassword_AreRejected_BeforeAnythingIsRead()
    {
        var fake = new Fake { Env = { ["PW"] = "x" } };
        using var _ = fake.Install();
        Assert.Contains("both supply the same password",
            UsageMessage(() => new ArgReader(["--password", "a", "--password-env", "PW"])));
        Assert.Contains("both supply the same password",
            UsageMessage(() => new ArgReader(["--out-password-prompt", "--out-password-file", "f"])));
        Assert.Contains("more than once", UsageMessage(() => new ArgReader(["--password", "a", "--password", "b"])));
        Assert.Empty(fake.Prompts);
        Assert.Equal(0, fake.StdinOpened);
    }

    [Fact]
    public void DifferentPasswords_MayUseDifferentSources()
    {
        var fake = new Fake { Env = { ["IN"] = "in-pw" } };
        fake.Typed.Enqueue("new-pw"); fake.Typed.Enqueue("new-pw");
        using var _ = fake.Install();
        var opts = new ArgReader(["--password-env", "IN", "--out-password-prompt"]);
        Assert.Equal("in-pw", opts.Password("--password"));
        Assert.Equal("new-pw", opts.Password("--out-password"));
        Assert.Null(opts.Password("--key-password"));
    }

    [Theory]
    [InlineData("--out-password-promt")]     // would otherwise swallow the next argument
    [InlineData("--passwrd-file")]
    [InlineData("--password-stdin")]
    [InlineData("--pfx-password")]
    [InlineData("--PASSWORD-FILES")]
    [InlineData("--key-passphrase")]
    public void MisspeltPasswordOption_IsRejected_NotSilentlyIgnored(string option)
    {
        using var _ = new Fake().Install();
        Assert.Contains("Unknown option " + option, UsageMessage(() => new ArgReader([option, "-o", "out.pfx"])));
        Assert.Contains("Unknown option " + option, UsageMessage(() => new ArgReader(["in.pem", option])));
    }

    [Fact]
    public void PasswordOptionWithoutItsValue_IsRejected()
    {
        using var _ = new Fake().Install();
        Assert.Contains("needs a value", UsageMessage(() => new ArgReader(["--password-file"])));
        Assert.Contains("needs a value", UsageMessage(() => new ArgReader(["--password-env", ""])));
    }

    [Fact]
    public void PromptOption_TakesNoValue_SoTheNextArgumentIsNotSwallowed()
    {
        var fake = new Fake(); fake.Typed.Enqueue("pw");
        using var _ = fake.Install();
        var opts = new ArgReader(["--password-prompt", "in.pfx", "-o", "out.pem"]);
        Assert.Equal(["in.pfx"], opts.Positionals);
        Assert.Equal("out.pem", opts.Get("--out"));
        Assert.Equal("pw", opts.Password("--password"));
    }

    [Fact]
    public void OptionsThatMerelyContainPass_AreNotMistakenForPasswords()
    {
        using var _ = new Fake().Install();
        var opts = new ArgReader(["--bypass-cache", "yes", "--compass", "north"]);
        Assert.Equal("yes", opts.Get("--bypass-cache"));
        Assert.Equal("north", opts.Get("--compass"));
    }

    [Fact]
    public void Get_RefusesToReadAPassword_SoACallSiteCannotSilentlyGetNull()
    {
        using var _ = new Fake().Install();
        var opts = new ArgReader(["--password", "x"]);
        Assert.Throws<InvalidOperationException>(() => opts.Get("--password"));
        Assert.Throws<InvalidOperationException>(() => opts.Get("--out-password-file"));
    }

    [Fact]
    public void ExitCodes_AMalformedCommandLineIsUsage1_AFailedSourceIsFailure2()
    {
        using var _ = new Fake().Install();
        Assert.Equal(CliRunner.ExitUsage, CliRunner.Run(["inspect", "x.pfx", "--passwrd-file", "f"]));
        Assert.Equal(CliRunner.ExitUsage, CliRunner.Run(["inspect", "x.pfx", "--password", "a", "--password-env", "B"]));
        Assert.Equal(CliRunner.ExitUsage, CliRunner.Run(["inspect", "x.pfx", "--password-file"]));
        // An ordinary option missing its value keeps the exit code it always had.
        Assert.Equal(CliRunner.ExitFailure, CliRunner.Run(["convert", "x.pem", "--to"]));
        var m = Prepare();
        try
        {
            Assert.Equal(CliRunner.ExitFailure, CliRunner.Run(["inspect", m.Pfx, "--password-env", "NOT_SET"]));
            Assert.Equal(CliRunner.ExitFailure, CliRunner.Run(["inspect", m.Pfx]));           // unchanged
            Assert.Equal(CliRunner.ExitOk, CliRunner.Run(["inspect", m.Pfx, "--password", Material.PfxPw]));
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    // ---------- file ----------

    [Theory]
    [InlineData("hunter2\n", "hunter2")]
    [InlineData("hunter2\r\n", "hunter2")]
    [InlineData("hunter2", "hunter2")]                       // no final newline
    [InlineData("  spaced out  \n", "  spaced out  ")]       // spaces are part of the password
    [InlineData("first\nsecond\n", "first")]                 // only the first line
    [InlineData("first\rsecond", "first")]                   // a lone CR ends the line too
    [InlineData("\r\n", "")]
    [InlineData("\n", "")]                                   // a blank line = empty on purpose
    [InlineData("\uFEFFbom\n", "bom")]                       // byte-order mark is not part of it
    [InlineData("pässwörd-🔑\n", "pässwörd-🔑")]
    public void File_GivesTheFirstLine_WithOnlyTheLineEndingRemoved(string content, string expected)
    {
        var dir = TempDir();
        try
        {
            using var _ = new Fake().Install();
            var opts = new ArgReader(["--password-file", PasswordFile(dir, content)]);
            Assert.Equal(expected, opts.Password("--password"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void File_Missing_Empty_OrTooLong_FailsCleanly_WithoutEchoingContent()
    {
        var dir = TempDir();
        try
        {
            using var _ = new Fake().Install();
            string missing = Message(() => new ArgReader(["--password-file", Path.Combine(dir, "nope.txt")]).Password("--password"));
            Assert.Contains("nope.txt", missing);
            Assert.Contains("does not exist", missing);

            Assert.Contains("is empty",
                Message(() => new ArgReader(["--password-file", PasswordFile(dir, "")]).Password("--password")));

            string secret = new string('x', PasswordSources.MaxLength + 1);
            string tooLong = Message(() => new ArgReader(["--password-file", PasswordFile(dir, secret, "long.txt")]).Password("--password"));
            Assert.Contains("longer than", tooLong);
            Assert.DoesNotContain("xxxx", tooLong);

            Assert.Contains("is a folder, not a file",
                Message(() => new ArgReader(["--password-file", dir]).Password("--password")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    public void TheLengthLimit_CountsThePassword_NotItsLineEnding(string ending)
    {
        var dir = TempDir();
        try
        {
            using var _ = new Fake().Install();
            string longest = new string('p', PasswordSources.MaxLength);
            Assert.Equal(longest, new ArgReader(
                ["--password-file", PasswordFile(dir, longest + ending)]).Password("--password"));
            Assert.Contains("longer than", Message(() => new ArgReader(
                ["--password-file", PasswordFile(dir, longest + "p" + ending, "over.txt")]).Password("--password")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void File_ReadableByOthers_Warns_ButStillWorks()
    {
        var dir = TempDir();
        try
        {
            var loose = PasswordFile(dir, "pw\n", "loose.txt");
            var tight = PasswordFile(dir, "pw\n", "tight.txt");
            var fake = new Fake { ReadableByOthers = { loose } };
            using var _ = fake.Install();
            Assert.Equal("pw", new ArgReader(["--password-file", tight]).Password("--password"));
            Assert.Empty(fake.Warnings);
            Assert.Equal("pw", new ArgReader(["--password-file", loose]).Password("--password"));
            Assert.Contains("loose.txt", Assert.Single(fake.Warnings));
            Assert.Contains("mode 600", fake.Warnings[0]);
            // The path is never echoed into something that looks like a command to paste.
            Assert.DoesNotContain(dir, fake.Warnings[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RealPermissionCheck_FlagsGroupOrOtherReadableFiles_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix mode bits; Windows uses ACLs.");
            return;
        }
        var dir = TempDir();
        try
        {
            var path = PasswordFile(dir, "pw\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.False(PasswordIo.Default.IsReadableByOthers(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.True(PasswordIo.Default.IsReadableByOthers(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.True(PasswordIo.Default.IsReadableByOthers(path));
            // Device paths are skipped by name, whatever their mode happens to be.
            Assert.False(PasswordIo.Default.IsReadableByOthers("/dev/stdin"));
            Assert.False(PasswordIo.Default.IsReadableByOthers(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- standard input ----------

    [Theory]
    [InlineData("piped\n", "piped")]
    [InlineData("piped", "piped")]
    [InlineData("\n", "")]
    public void FileDash_ReadsOneLineFromStandardInput(string stdin, string expected)
    {
        var fake = new Fake { Stdin = stdin, Terminal = false };
        using var _ = fake.Install();
        var opts = new ArgReader(["--password-file", "-"]);
        Assert.Equal(expected, opts.Password("--password"));
        Assert.Equal(expected, opts.Password("--password"));    // cached, not read twice
    }

    [Fact]
    public void FileDash_WithNothingPiped_OrFromAKeyboard_Fails()
    {
        var fake = new Fake { Stdin = "", Terminal = false };
        using (fake.Install())
            Assert.Contains("No password arrived on standard input",
                Message(() => new ArgReader(["--password-file", "-"]).Password("--password")));

        var keyboard = new Fake { Stdin = "visible\n", Terminal = true };
        using (keyboard.Install())
        {
            string m = Message(() => new ArgReader(["--password-file", "-"]).Password("--password"));
            Assert.Contains("would be shown as you type", m);
            Assert.Contains("--password-prompt", m);
            Assert.Equal(0, keyboard.StdinOpened);
        }
    }

    [Fact]
    public void StandardInput_HasTheSameLengthLimit()
    {
        var fake = new Fake { Stdin = new string('p', PasswordSources.MaxLength + 1) + "\n", Terminal = false };
        using var _ = fake.Install();
        Assert.Contains("longer than", Message(() => new ArgReader(["--password-file", "-"]).Password("--password")));
    }

    [Theory]
    [InlineData("/dev/stdin")]
    [InlineData("/dev/fd/0")]
    public void TheStandardInputDevice_IsTreatedExactlyLikeDash(string device)
    {
        var piped = new Fake { Stdin = "piped\n", Terminal = false };
        using (piped.Install())
            Assert.Equal("piped", new ArgReader(["--password-file", device]).Password("--password"));
        var keyboard = new Fake { Terminal = true };
        using (keyboard.Install())
        {
            Assert.Contains("would be shown as you type",
                Message(() => new ArgReader(["--password-file", device]).Password("--password")));
            Assert.Contains("both read standard input",
                UsageMessage(() => new ArgReader(["--password-file", device, "--out-password-file", "-"])));
        }
    }

    [Fact]
    public void TheTerminalDevice_IsRefusedAsAPasswordFile()
    {
        using var _ = new Fake().Install();
        string m = Message(() => new ArgReader(["--password-file", "/dev/tty"]).Password("--password"));
        Assert.Contains("show the password as you type", m);
        Assert.Contains("--password-prompt", m);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con:")]
    [InlineData(@"\\.\CON")]
    [InlineData("conin$")]
    [InlineData(@"\\.\CONIN$")]
    public void TheWindowsConsole_IsRefusedAsAPasswordFile_InAnySpelling(string name)
    {
        using var _ = new Fake { Windows = true }.Install();
        string m = Message(() => new ArgReader(["--password-file", name]).Password("--password"));
        Assert.Contains("show the password as you type", m);
        Assert.DoesNotContain("-prompt", m);        // not offered on Windows
        Assert.False(PasswordSources.IsConsoleName("console.txt"));
        Assert.False(PasswordSources.IsConsoleName("icon"));
    }

    [Fact]
    public void APathThatTurnsOutToBeATerminal_IsRefused_WhateverItIsCalled()
    {
        // The real opener asks the system whether the opened file is a terminal; here the
        // fake reports that it is, as for /dev/./tty or a symlink to the terminal.
        using var _ = PasswordIo.Use(PasswordIo.Default with
        {
            OpenFile = _ => throw new TerminalDeviceException(),
        });
        string m = Message(() => new ArgReader(["--password-file", "/dev/./tty"]).Password("--password"));
        Assert.Contains("show the password as you type", m);
    }

    [Fact]
    public void AFileThatIsNotText_FailsInsteadOfBecomingADifferentPassword()
    {
        var dir = TempDir();
        try
        {
            using var _ = new Fake().Install();
            var path = Path.Combine(dir, "binary.bin");
            File.WriteAllBytes(path, [0x70, 0x77, 0xFF, 0xFE, 0x0A]);     // "pw", two invalid bytes, LF
            Assert.Contains("not valid text",
                Message(() => new ArgReader(["--password-file", path]).Password("--password")));

            var utf16 = Path.Combine(dir, "utf16.txt");                    // as Windows PowerShell 5 writes
            File.WriteAllText(utf16, "pässword\r\n", System.Text.Encoding.Unicode);
            Assert.Equal("pässword", new ArgReader(["--password-file", utf16]).Password("--password"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StandardInput_CanSupplyOnlyOnePassword_AndNotAlongsideAPrompt()
    {
        var fake = new Fake { Stdin = "a\nb\n", Terminal = false };
        using var _ = fake.Install();
        Assert.Contains("both read standard input",
            UsageMessage(() => new ArgReader(["--password-file", "-", "--out-password-file", "-"])));
        Assert.Contains("cannot be used together",
            UsageMessage(() => new ArgReader(["--password-file", "-", "--out-password-prompt"])));
        Assert.Equal(0, fake.StdinOpened);
    }

    // ---------- environment ----------

    [Fact]
    public void Env_ReadsTheVariable_AndAnUnsetOneFails_NamingIt()
    {
        var fake = new Fake { Env = { ["CC_PW"] = "from-env", ["EMPTY"] = "" } };
        using var _ = fake.Install();
        Assert.Equal("from-env", new ArgReader(["--password-env", "CC_PW"]).Password("--password"));
        Assert.Equal("", new ArgReader(["--password-env", "EMPTY"]).Password("--password"));
        string m = Message(() => new ArgReader(["--password-env", "NOT_SET"]).Password("--password"));
        Assert.Contains("NOT_SET", m);
        Assert.Contains("is not set", m);
    }

    // ---------- prompt ----------

    [Fact]
    public void Prompt_AsksOnce_HoweverOftenThePasswordIsUsed()
    {
        var fake = new Fake(); fake.Typed.Enqueue("typed");
        using var _ = fake.Install();
        var opts = new ArgReader(["--password-prompt"]);
        Assert.Empty(fake.Prompts);                         // nothing is asked until it is needed
        Assert.Equal("typed", opts.Password("--password"));
        Assert.Equal("typed", opts.Password("--password"));
        Assert.Single(fake.Prompts);
    }

    [Fact]
    public void Prompt_PressingEnterAtOnce_GivesAnEmptyPassword_NotNull()
    {
        var fake = new Fake(); fake.Typed.Enqueue("");
        using var _ = fake.Install();
        Assert.Equal("", new ArgReader(["--password-prompt"]).Password("--password"));
    }

    [Fact]
    public void NewPasswordPrompt_AsksTwice_AndAMismatchFails()
    {
        var ok = new Fake(); ok.Typed.Enqueue("same"); ok.Typed.Enqueue("same");
        using (ok.Install())
        {
            Assert.Equal("same", new ArgReader(["--out-password-prompt"]).Password("--out-password"));
            Assert.Equal(2, ok.Prompts.Count);
        }
        var bad = new Fake(); bad.Typed.Enqueue("one"); bad.Typed.Enqueue("other");
        using (bad.Install())
            Assert.Contains("did not match",
                Message(() => new ArgReader(["--out-password-prompt"]).Password("--out-password")));
    }

    [Fact]
    public void Prompt_NeedsAKeyboard_AndIsRefusedOnWindows_PointingAtTheAlternatives()
    {
        var piped = new Fake { Terminal = false };
        using (piped.Install())
        {
            string m = Message(() => new ArgReader(["--key-password-prompt"]).Password("--key-password"));
            Assert.Contains("standard input is redirected", m);
            Assert.Contains("--key-password-file", m);
            Assert.Contains("--key-password-env", m);
            Assert.Empty(piped.Prompts);
        }
        var windows = new Fake { Windows = true };
        using (windows.Install())
        {
            string m = Message(() => new ArgReader(["--password-prompt"]).Password("--password"));
            Assert.Contains("not available on Windows", m);
            Assert.Contains("--password-file", m);
            Assert.Empty(windows.Prompts);
        }
    }

    // ---------- through the real commands ----------

    private sealed record Material(string Dir, string Cert, string Key, string EncryptedKey, string Pfx)
    {
        public const string KeyPw = "key-pw";
        public const string PfxPw = "pfx-pw";
        public string Out(string name) => Path.Combine(Dir, name);
    }

    private static Material Prepare()
    {
        var dir = TempDir();
        try { return Prepare(dir); }
        catch { Directory.Delete(dir, true); throw; }   // do not leave a half-built fixture behind
    }

    private static Material Prepare(string dir)
    {
        using var key = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
        using var cert = Generator.CreateSelfSigned(key.Key, new CertSpec
        {
            CommonName = "device.test", IsCertificateAuthority = true, ValidityDays = 30,
        });
        var m = new Material(dir, Path.Combine(dir, "device.pem"), Path.Combine(dir, "device.key"),
            Path.Combine(dir, "device-enc.key"), Path.Combine(dir, "device.pfx"));
        File.WriteAllText(m.Cert, cert.ExportCertificatePem() + "\n");
        File.WriteAllText(m.Key, KeyTools.ExportPem(key.Key, KeyOutputFormat.Pkcs8Pem));
        File.WriteAllText(m.EncryptedKey,
            KeyTools.ExportPem(key.Key, KeyOutputFormat.Pkcs8EncryptedPem, Material.KeyPw));
        File.WriteAllBytes(m.Pfx, Converter.Export([cert], CertOutputFormat.Pkcs12,
            new ExportOptions { Password = Material.PfxPw, PrivateKey = key.Key }));
        return m;
    }

    [Fact]
    public void Convert_WritesAPfx_ProtectedByThePasswordFromAFile()
    {
        var m = Prepare();
        try
        {
            using var _ = new Fake().Install();
            var outPfx = m.Out("out.pfx");
            int rc = Commands.Convert([m.Cert, "--key", m.Key, "-o", outPfx,
                "--out-password-file", PasswordFile(m.Dir, "file-pw\n")]);
            Assert.Equal(0, rc);
            using var reopened = ContentLoader.LoadFile(outPfx, "file-pw");
            Assert.Single(reopened.PrivateKeys);
            Assert.Throws<InvalidPasswordException>(() => ContentLoader.LoadFile(outPfx, "wrong"));
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void Convert_WithATypedNewPassword_AsksTwice_AndAMismatchWritesNothing()
    {
        var m = Prepare();
        try
        {
            var outPfx = m.Out("out.pfx");
            var bad = new Fake(); bad.Typed.Enqueue("one"); bad.Typed.Enqueue("two");
            using (bad.Install())
                Assert.Throws<CertConvertException>(() => Commands.Convert(
                    [m.Cert, "--key", m.Key, "-o", outPfx, "--out-password-prompt"]));
            Assert.False(File.Exists(outPfx));

            var ok = new Fake(); ok.Typed.Enqueue("typed-pw"); ok.Typed.Enqueue("typed-pw");
            using (ok.Install())
                Assert.Equal(0, Commands.Convert(
                    [m.Cert, "--key", m.Key, "-o", outPfx, "--out-password-prompt"]));
            Assert.Equal(2, ok.Prompts.Count);              // once, plus the confirmation
            using var reopened = ContentLoader.LoadFile(outPfx, "typed-pw");
            Assert.Single(reopened.PrivateKeys);
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void KeyConvert_OpensAnEncryptedPfx_WithThePasswordFromTheEnvironment()
    {
        var m = Prepare();
        try
        {
            var fake = new Fake { Env = { ["CC_PFX_PW"] = Material.PfxPw } };
            using var _ = fake.Install();
            var outKey = m.Out("extracted.key");
            Assert.Equal(0, Commands.KeyConvert([m.Pfx, "--password-env", "CC_PFX_PW", "-o", outKey]));
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(outKey));
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void KeyConvert_EncryptsTheOutput_WithAPipedNewPassword()
    {
        var m = Prepare();
        try
        {
            var fake = new Fake { Stdin = "piped-pw\n", Terminal = false };
            using var _ = fake.Install();
            var outKey = m.Out("re-encrypted.key");
            Assert.Equal(0, Commands.KeyConvert(
                [m.Key, "-o", outKey, "--to", "pkcs8-enc", "--out-password-file", "-"]));
            using var reopened = ContentLoader.LoadFile(outKey, "piped-pw");
            Assert.Single(reopened.PrivateKeys);
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void Convert_KeyPassword_FallsBackToThePassword_AndPromptsOnlyOnce()
    {
        var m = Prepare();
        try
        {
            var fake = new Fake(); fake.Typed.Enqueue(Material.KeyPw);
            using var _ = fake.Install();
            var outPfx = m.Out("out.pfx");
            int rc = Commands.Convert([m.Cert, "--key", m.EncryptedKey, "--password-prompt",
                "-o", outPfx, "--out-password", "x"]);
            Assert.Equal(0, rc);
            Assert.Single(fake.Prompts);
            using var reopened = ContentLoader.LoadFile(outPfx, "x");
            Assert.Single(reopened.PrivateKeys);
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void Convert_AnExplicitKeyPassword_WinsOverTheFallback()
    {
        var m = Prepare();
        try
        {
            var fake = new Fake { Env = { ["KEYPW"] = Material.KeyPw } };
            using var _ = fake.Install();
            var outPfx = m.Out("out.pfx");
            // --password is wrong for the key; --key-password is right and must be the one used.
            int rc = Commands.Convert([m.Cert, "--key", m.EncryptedKey, "--password", "wrong",
                "--key-password-env", "KEYPW", "-o", outPfx, "--out-password", "x"]);
            Assert.Equal(0, rc);
            using var reopened = ContentLoader.LoadFile(outPfx, "x");
            Assert.Single(reopened.PrivateKeys);
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void ANewPasswordThatWouldBeDiscarded_IsNeverAskedFor()
    {
        var m = Prepare();
        try
        {
            var fake = new Fake();          // nothing queued: any prompt would throw
            using var _ = fake.Install();
            // An unencrypted key format takes no password.
            Assert.Equal(0, Commands.KeyConvert([m.Key, "-o", m.Out("plain.key"), "--out-password-prompt"]));
            // Neither does a PEM certificate.
            Assert.Equal(0, Commands.Convert([m.Pfx, "--password", Material.PfxPw,
                "-o", m.Out("certs.pem"), "--out-password-prompt"]));
            Assert.Empty(fake.Prompts);
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(m.Out("plain.key")));
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void KeyMatch_UsesTheKeyPasswordForm_ForTheKeyFile()
    {
        var m = Prepare();
        try
        {
            var fake = new Fake { Env = { ["KEYPW"] = Material.KeyPw } };
            using var _ = fake.Install();
            Assert.Equal(0, Commands.KeyMatch(
                ["--cert", m.Cert, "--key", m.EncryptedKey, "--key-password-env", "KEYPW"]));
        }
        finally { Directory.Delete(m.Dir, true); }
    }

    [Fact]
    public void CaSign_UnlocksTheCaKey_WithThePasswordFromAFile()
    {
        var m = Prepare();
        try
        {
            using var devKey = Generator.CreateKey(KeyAlgorithmChoice.EcP256);
            var csr = m.Out("device.csr");
            File.WriteAllText(csr, Generator.CreateCsrPem(devKey.Key, new CertSpec
            {
                CommonName = "leaf.test", DnsNames = ["leaf.test"],
            }));
            using var _ = new Fake().Install();
            var issued = m.Out("issued.pem");
            int rc = Commands.CaSign(["--csr", csr, "--ca-cert", m.Cert, "--ca-key", m.EncryptedKey,
                "--ca-key-password-file", PasswordFile(m.Dir, Material.KeyPw + "\n"),
                "--dns", "leaf.test", "-o", issued]);
            Assert.Equal(0, rc);
            using var result = ContentLoader.LoadFile(issued);
            Assert.Equal("leaf.test", Inspector.Inspect(result.Certificates[0]).DisplayName);
        }
        finally { Directory.Delete(m.Dir, true); }
    }
}

<p align="center">
  <img src="src/CertConvert/Assets/icon-256.png" width="110" alt="CertConvert icon"/>
</p>
<h1 align="center">CertConvert</h1>
<p align="center"><b>Certificate Toolbox</b> — convert, chain, inspect and generate X.509 certificates without installing OpenSSL.</p>

<p align="center">
  <img src="https://img.shields.io/badge/licence-MIT-green" alt="MIT licence"/>
  <img src="https://img.shields.io/badge/platform-macOS%20%7C%20Windows-blue" alt="Platforms: macOS and Windows"/>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/tests-253%20passing-brightgreen" alt="253 tests passing"/>
  <a href="https://ko-fi.com/jwalkes"><img src="https://img.shields.io/badge/Ko--fi-support-FF5E5B?logo=kofi&logoColor=white" alt="Support on Ko-fi"/></a>
</p>

CertConvert is a single self-contained desktop app (and command-line tool)
that runs entirely offline. It exists for the times you need to turn a `.pem`
into a `.cer`, assemble a root → intermediate → device chain, or bundle a key
and certificate into a `.pfx`, but you're on a machine where installing
OpenSSL and running shell commands isn't an option.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/screenshots/inspect-dark.png">
    <img src="design/screenshots/inspect-light.png" width="880"
         alt="CertConvert inspecting a certificate: subject, issuer, validity, SANs, key usage and fingerprints decoded from a PEM file"/>
  </picture>
</p>

## What it does

- **Inspect** any certificate, key, CSR or bundle — subject, issuer, validity,
  SANs, key usage, fingerprints and more. Signing requests decode in full, every
  certificate gets a **health check** and you can ask whether a certificate
  covers a given **host name**.
- **Convert** between PEM, DER (`.cer`/`.der`), PKCS #7 (`.p7b`) and
  PKCS #12 (`.pfx`/`.p12`), in both directions.
- **Chain** certificates: drop root, intermediate and device certs in any
  order, have them ordered automatically, validate the chain offline, then
  export it as a PEM bundle, P7B or PFX.
- **Keys**: convert private-key formats (PKCS #8, PKCS #1, SEC 1, encrypted
  or not), and check whether a key matches a certificate.
- **Generate** keys, CSRs and self-signed certificates (RSA or ECDSA), with
  SANs and CA options — the `openssl req` workflows.

Formats are detected from file *content*, not extensions, so misnamed
files — the `.cer` that's secretly PEM — just work.

## Security posture

For a tool that handles private keys, the dependency surface is the whole
story:

- **All cryptography is the .NET platform's own libraries**
  (`System.Security.Cryptography`). There is no third-party crypto code. The
  only third-party dependencies are the UI framework (Avalonia) and the MVVM
  helper (CommunityToolkit.Mvvm) — neither touches key material.
- **It never uses the network on its own.** No telemetry, no outbound
  connection of any kind. The single exception is checking GitHub for a newer
  version — which is **off by default**, and otherwise happens only when you
  click **Check For Updates** (or run `certconvert update`). Your certificates
  and keys are never uploaded anywhere, ever.
- **Private keys stay in memory.** Keys loaded from PKCS #12 files are handled
  in process and are never imported into the operating-system key store.
- **It only writes the files you ask it to** (plus one small preferences file —
  see below).

## Where things live

Almost nowhere — by design. CertConvert keeps no cache, no logs and no history.
The only files it ever creates are the ones you explicitly save, plus a single
small preferences file holding one setting (whether to check for updates on
launch — nothing sensitive):

| Platform | Preferences file |
|---|---|
| macOS | `~/Library/Application Support/CertConvert/settings.json` |
| Windows | `%APPDATA%\CertConvert\settings.json` |

Delete it any time; the app recreates it with defaults.

## Install & run

CertConvert is available on both app stores — signed, with automatic updates:

| Platform | Store |
|---|---|
| macOS | [Mac App Store](https://apps.apple.com/app/id6791724651) |
| Windows | [Microsoft Store](https://apps.microsoft.com/detail/9NT6HCG0JBFV) |

Prefer to build it yourself? See [From source](#from-source) below.

### From source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/jermainewalkes/certconvert.git
cd certconvert
dotnet run --project src/CertConvert    # launches the GUI
```

## Command line

A copy built from source is a CLI when given arguments and a GUI when not. Run
it with `--help` for the full list; a few examples:

> The Mac App Store and Microsoft Store editions are sandboxed — a sandboxed
> process cannot open files named on the command line, so those editions are the
> graphical app only. Build from source for the CLI.

```bash
certconvert inspect device.pem
certconvert inspect device.pem --hostname www.example.com   # does it cover this host?
certconvert check chain.pem                             # health check; non-zero exit on any critical finding
certconvert convert device.pem -o device.cer            # PEM → DER
certconvert convert bundle.p7b -o bundle.pem            # PKCS#7 → PEM
certconvert chain build device.pem ca.pem root.pem -o chain.pfx \
            --key device.key --out-password-prompt          # asks for the new password, unseen
certconvert chain verify chain.p7b
certconvert key convert device.key -o device_pkcs8.key --to pkcs8
certconvert key match --cert device.pem --key device.key
certconvert gen selfsigned --new-key p256 --key-out dev.key \
            --cn device.local --dns device.local -o dev.pem
certconvert update                                      # check GitHub for a newer version
certconvert update --install                            # download, verify and apply it
```

Exit codes: `0` success, `1` usage error (including a misspelt password option),
`2` failure (including an invalid chain or a key that does not match).

### Passwords

Every password option (`--password`, `--key-password`, `--out-password` and
`--ca-key-password`) can be given four ways. Use one per password:

| Form | What it does |
|---|---|
| `--password-prompt` | Asks on the terminal without showing what you type. A new password (`--out-password-prompt`) is asked for twice. macOS and Linux only. |
| `--password-file <file>` | Reads the first line of a file. Use `-` to read it from standard input, for example piped from a password manager. |
| `--password-env <NAME>` | Reads an environment variable, which suits CI secrets. |
| `--password <value>` | Takes it from the command line itself. Avoid this for a password that matters: it is saved in shell history and other processes can see it. |

```bash
certconvert inspect device.pfx --password-prompt
pass show certs/device | certconvert inspect device.pfx --password-file -
certconvert convert device.pem --key device.key -o device.pfx --out-password-env PFX_PASSWORD
```

On Windows the app is a windowed program that shares the console with your
shell, so it cannot read typed keys reliably and the prompt form is refused
there. Use the file or environment form. In PowerShell:

```powershell
$env:PFX_PASSWORD = Read-Host -MaskInput 'New password'
certconvert convert device.pem --key device.key -o device.pfx --out-password-env PFX_PASSWORD
Remove-Item Env:PFX_PASSWORD      # do not leave it in the session
```

An environment variable is visible to other programs you run as the same user,
and a password file to anyone who can read it, so keep the file private
(`chmod 600`). CertConvert warns when a password file is readable by others.

## Troubleshooting

- **macOS: "CertConvert can't be opened"** — only applies to a self-compiled
  build (it is unsigned): right-click the app → **Open** (one-time), or allow
  it under System Settings → Privacy & Security. The Mac App Store edition is
  signed and opens normally.
- **Windows: "Windows protected your PC"** — only applies to self-compiled
  builds (SmartScreen on an unsigned binary; **More info → Run anyway**,
  one-time). The Microsoft Store edition is signed and shows no warning.
- **"…is password-protected"** — the file is an encrypted PFX or key; type
  the password in the password field and use Unlock (GUI), or on the CLI give it
  with `--password-prompt`, `--password-file` or `--password-env` (see
  [Passwords](#passwords)). Errors always name the file that needs it.
- **Windows CLI output interleaves with the prompt** — the exe is a GUI
  program attaching to your console; press Enter to get the prompt back.
- **Which build am I running?** — `certconvert --version` prints the version
  plus the exact git commit it was built from.
- **Updates** — the Mac App Store and Microsoft Store editions update
  themselves. A self-compiled build has no auto-update; rebuild from source to
  update. (The legacy in-app self-updater, present in older GitHub builds, kept
  a recovery copy — `CertConvert.app.bak` / `CertConvert.exe.old` — beside the
  app until the next launch.)
- **"Check For Updates" can't reach GitHub** — that check needs internet; on an
  offline machine it simply reports the failure and changes nothing. Everything
  else works offline.

## Development

```
src/CertConvert.Core/   all certificate/key logic — no UI dependencies
src/CertConvert/        Avalonia GUI + CLI (Cli/) in one executable
tests/                  Core unit tests (incl. OpenSSL interop fixtures)
                        and headless Avalonia UI tests
build/                  publish scripts, icon generation, dev-run helper
```

```bash
dotnet test                   # full suite
./build-in-docker.sh          # same suites + Release build in Docker — no host SDK needed
build/publish.sh              # self-contained builds: osx-x64, osx-arm64, win-x64
build/run-dev-app.sh          # macOS: run the Debug build as a bundled .app
```

The release process is documented in [build/RELEASING.md](build/RELEASING.md).

## Accessibility

Accessibility is a launch requirement, not an afterthought. Every control is
reachable by keyboard, icon-only and ambiguous controls carry screen-reader
labels, and operation results are announced through live regions. If anything
is awkward with assistive technology, please raise an issue — accessibility
problems are treated as bugs.

## Support

CertConvert is free and open source under the [MIT licence](LICENSE). If it
saves you time, you can [buy me a coffee on Ko-fi](https://ko-fi.com/jwalkes) ☕

## Roadmap

Signed and notarised macOS builds, a Windows installer, automated release
builds, signing CSRs with your own CA, and a universal macOS binary.

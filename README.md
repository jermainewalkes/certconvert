# CertConvert

Convert, chain, inspect and generate X.509 certificates without installing
OpenSSL. CertConvert is a single self-contained desktop app (and command-line
tool) that runs entirely offline on macOS — Intel and Apple Silicon — and
Windows.

It exists for the times you need to turn a `.pem` into a `.cer`, assemble a
root → intermediate → device chain, or bundle a key and certificate into a
`.pfx`, but you're on a machine where installing OpenSSL and running shell
commands isn't an option.

## What it does

- **Inspect** any certificate, key, CSR or bundle — drag it in and read the
  subject, issuer, validity, SANs, key usage, fingerprints and more.
- **Convert** between PEM, DER (`.cer`/`.der`), PKCS #7 (`.p7b`) and
  PKCS #12 (`.pfx`/`.p12`), in both directions.
- **Chain** certificates: drop root, intermediate and device certs in any
  order, have them ordered automatically, validate the chain offline, then
  export it as a PEM bundle, P7B or PFX.
- **Keys**: convert private-key formats (PKCS #8, PKCS #1, SEC 1, encrypted
  or not), and check whether a key matches a certificate.
- **Generate** keys, CSRs and self-signed certificates (RSA or ECDSA), with
  SANs and CA options — the `openssl req` workflows.

## Security posture

For a tool that handles private keys, the dependency surface is the whole
story:

- **All cryptography is the .NET platform's own libraries**
  (`System.Security.Cryptography`). There is no third-party crypto code. The
  only third-party dependencies are the UI framework (Avalonia) and the MVVM
  helper (CommunityToolkit.Mvvm) — neither touches key material.
- **It never uses the network.** There is no telemetry, no update check, no
  outbound connection of any kind. The only thing that opens a browser is the
  Ko-fi link on the About tab, and only when you click it.
- **Private keys stay in memory.** Keys loaded from PKCS #12 files are handled
  in process and are never imported into the operating-system key store.
- **It only writes the files you ask it to.**

## Running it

### From a released build (no .NET needed)

Downloads are self-contained — the .NET runtime is bundled, so nothing else
needs installing.

- **macOS**: unzip `CertConvert-<version>-osx-x64.zip` (Intel) or
  `...-osx-arm64.zip` (Apple Silicon) and move `CertConvert.app` to
  Applications. The build is unsigned, so the first launch needs
  right-click → **Open** (or **System Settings → Privacy & Security → Open
  Anyway**) to get past Gatekeeper.
- **Windows**: unzip `CertConvert-<version>-win-x64.zip` and run
  `CertConvert.exe`.

### From source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone <this repo>
cd CertConvert
dotnet run --project src/CertConvert    # launches the GUI
dotnet test                             # runs the test suite
```

## Command line

The same executable is a CLI when given arguments and a GUI when not. Run it
with `--help` for the full list; a few examples:

```bash
certconvert inspect device.pem
certconvert convert device.pem -o device.cer            # PEM → DER
certconvert convert bundle.p7b -o bundle.pem            # PKCS#7 → PEM
certconvert chain build device.pem ca.pem root.pem -o chain.pfx \
            --key device.key --out-password secret
certconvert chain verify chain.p7b
certconvert key convert device.key -o device_pkcs8.key --to pkcs8
certconvert key match --cert device.pem --key device.key
certconvert gen selfsigned --new-key p256 --key-out dev.key \
            --cn device.local --dns device.local -o dev.pem
```

Exit codes: `0` success, `1` usage error, `2` failure (including an invalid
chain or a key that does not match).

On Windows the executable is a GUI-subsystem binary that attaches to the
calling console when run with arguments; if the shell prompt returns before
the output prints, press Enter.

## Building packages

```bash
build/publish.sh              # osx-x64, osx-arm64 and win-x64
build/publish.sh osx-arm64    # just one target
```

Artifacts (single-file self-contained builds, macOS `.app` zips and a Windows
zip) land in `artifacts/`.

## Accessibility

Accessibility is a launch requirement, not an afterthought. Every control is
reachable by keyboard, icon-only and ambiguous controls carry screen-reader
labels, and operation results are announced through live regions. If anything
is awkward with assistive technology, please raise an issue — accessibility
problems are treated as bugs.

## Support

CertConvert is free and open source under the [MIT licence](LICENSE). If it
saves you time, a coffee is appreciated — see the Ko-fi link on the About tab.

## Roadmap

Not yet done, tracked for later: signed and notarised macOS builds, a Windows
installer, automated release builds, and a universal (Intel + Apple Silicon)
macOS binary.

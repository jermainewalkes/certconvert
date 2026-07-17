# CertConvert — Microsoft Store submission prep

Paste-ready copy for Partner Center → CertConvert (Store ID 9NT6HCG0JBFV) →
Submissions. Package: `artifacts/msix/CertConvert-1.1.0.msix` (built 17 Jul,
unsigned — the Store signs it; identity JermaineWalkes.CertConvert already
matches). Mirrors `mac-app-store-listing.md`, adapted for Windows and the
Partner Center field set.

## Packages

- Upload `CertConvert-1.1.0.msix` under **Packages**. Device family:
  Windows 10/11 desktop (x64). No other families.

## Properties

- **Category:** Developer tools
- **Subcategory:** (none needed)
- **Privacy policy URL:** https://certconvert.com/privacy.html *(required —
  same policy as Mac; the store build collects nothing)*
- **Website:** https://certconvert.com
- **Support contact info:** https://certconvert.com/contact.html
- **System requirements:** nothing beyond defaults (x64, Windows 10 1809+).

## Age ratings

IARC questionnaire: answer **No** to everything (no violence, no user
interaction/chat, no data sharing, no purchases inside the app, no ads).
Result should be rated for all ages (PEGI 3 / ESRB E equivalents).

## Pricing and availability

- **Price:** $9.99 base tier. Check the GBP auto-conversion and override to
  **£8.99** if it lands elsewhere (matches the Mac App Store price).
- **Markets:** all. **Free trial:** none.
- **Visibility:** Public. **Release:** publish as soon as it passes
  certification (or hold for manual release to line up with the Mac launch).

## Store listing (English (United Kingdom))

**Description** (≤10,000 chars):

> CertConvert is a self-contained certificate toolbox for Windows. It does the everyday X.509 jobs — converting formats, assembling and validating chains, inspecting certificates and generating keys — entirely on your own machine, with nothing uploaded anywhere.
>
> Unlike the online certificate converters, CertConvert never sends your files or private keys to a server. Everything happens locally.
>
> WHAT IT DOES
> • Inspect any certificate, key, CSR or bundle — drag it in and read the subject, issuer, validity, SANs, key usage and fingerprints.
> • Convert between PEM, DER (.cer/.der), PKCS #7 (.p7b) and PKCS #12 (.pfx/.p12), in both directions.
> • Build chains — drop root, intermediate and device certificates in any order, have them ordered and validated automatically, then export as a PEM bundle, P7B or PFX.
> • Convert private-key formats (PKCS #8, PKCS #1, SEC 1, encrypted or not) and check whether a key matches a certificate.
> • Generate keys, certificate signing requests and self-signed certificates (RSA or ECDSA) with SANs and CA options.
> • Use it from the command line too — the same app is a full CLI when run with arguments (run it with --help in a terminal).
>
> BUILT FOR TRUST
> All cryptography uses the operating system's own libraries — there is no third-party cryptographic code. The app makes no network connections and collects no data. Private keys loaded from PKCS #12 files are handled in memory and are never imported into your Windows certificate store.
>
> Formats are detected from file content, not extensions, so the .cer that is secretly PEM just works.

**What's new in this version** (release notes):

> Redesigned Generate: choose a self-signed certificate or a signing request first, and the form shows only the fields that output needs.

**Product features** (bullet list field, ≤200 chars each):

- Inspect any certificate, key, CSR or bundle — subject, issuer, validity, SANs, key usage and fingerprints
- Convert PEM, DER (.cer/.der), PKCS #7 (.p7b) and PKCS #12 (.pfx/.p12) in both directions
- Build certificate chains: auto-order, validate offline, export as PEM bundle, P7B or PFX
- Convert private-key formats (PKCS #8, PKCS #1, SEC 1) and check key–certificate matches
- Generate keys, CSRs and self-signed certificates (RSA or ECDSA) with SANs and CA options
- GUI and command line in one app
- Fully offline: no uploads, no account, no telemetry

**Search terms** (max 7, ≤30 chars each):

`certificate converter` · `pem to pfx` · `x509` · `csr` · `pkcs12` ·
`ssl certificate` · `certificate chain`

**Short description** (supplemental, ≤270 — Short title and Voice title stay blank; they are Xbox fields):

> Convert, chain, inspect and generate X.509 certificates entirely on your own PC. PEM, DER, PKCS #7 and PKCS #12 in both directions, offline chain validation and key tools — no uploads, no account, your private keys never leave the machine.

**Copyright and trademark info:**

> © 2026 Jermaine Walkes

**Additional licence terms:** leave as Standard Application Licence Terms.

## Screenshots — READY

Seven 1920×1080 PNGs in `design/msstore-screenshots/` (Windows-rendered, store
variant, captured on the build VM 17 Jul): 01-inspect … 05-generate, 06-chain-validated, 07-keys-loaded. Upload in
that order — Inspect first, it's the strongest opener. Regenerate with the VM
capture flow (`CERTCONVERT_CAPTURE_SIZE=1920x1080`; note the capture dir is
resolved against the TEST RUNNER's working directory, so pass an absolute path
or fetch from `tests/.../bin/Debug/net10.0/design/msstore-screenshots`).

## Store logos

In `design/msstore-logos/`: **boxart-1080.png** (1:1 box art) and
**poster-1440x2160.png** (2:3 poster art). Regenerate with
`python3 build/make-icons.py --msstore-logos`. These are the LISTING images —
the package tile assets in build/msix/assets are separate and already inside
the MSIX.

## Notes

- Nothing in the listing mentions other stores or the GitHub build (kept
  consistent with the Mac listing; no store-policy need to mention them).
- No OpenSSL mention anywhere in Store copy (trademark caution, same call as
  the Mac keywords).
- The MSIX runs full-trust (`runFullTrust`) — if certification asks why:
  it is a desktop Avalonia app packaged with MSIX; it uses only user-selected
  file access and no restricted capabilities beyond full trust itself.

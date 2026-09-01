# CertConvert — Mac App Store submission prep

Everything needed to complete the App Store Connect listing for **CertConvert**
(macOS), staged for review. **Nothing has been submitted.** Fields marked
**[DECIDE]** need your confirmation before submitting.

## Build

- Package: `artifacts/mas/CertConvert-1.3.1.pkg` (42 MB, arm64, store variant —
  self-updater and Ko-fi removed), signed with *3rd Party Mac Developer
  Installer*. Built by `build/make-mas-pkg.sh`.
- **Upload** (does not submit — sends the build to App Store Connect so it
  appears under the version; you then attach it and submit):
  ```bash
  xcrun altool --upload-app -f artifacts/mas/CertConvert-1.3.1.pkg -t macos \
    --apiKey X459QTHUM2 --apiIssuer 0d5235ae-7e19-4001-8eed-20689be6be83
  ```
- **[DECIDE]** arm64-only. Intel Macs won't be offered it. Ship arm64 now, or
  hold for a universal build (deferred — non-trivial with single-file). Rec: ship arm64.

## App information (set once)

- **Name:** CertConvert
- **Subtitle** (≤30): `Offline certificate toolbox`
- **Bundle ID:** com.certconvert.app · **SKU:** certconvert-mac
- **Primary category:** Developer Tools · **Secondary:** Utilities
- **Age rating:** 4+ (no objectionable content)
- **Content rights:** does not use third-party content → No

## Pricing and availability

- **[DECIDE] Price:** £8.99 / $9.99 tier (agreed earlier). All territories.
- Small Business Program: confirm enrolment for the 15% rate.

## Version 1.3.1 — listing text

**Promotional text** (≤170, updatable anytime without review):
> Convert, chain, inspect, generate and sign X.509 certificates entirely on your Mac. No uploads, no command line, no OpenSSL — your keys never leave the machine.

**Keywords** (≤100 chars, comma-separated — deliberately NO "OpenSSL" or other
trademarks in this field):
> certificate,pem,pfx,der,pkcs12,x509,ssl,tls,csr,convert,keychain,pki,crypto,p7b

**Description** (≤4000):
> CertConvert is a self-contained certificate toolbox for macOS. It does the everyday X.509 jobs — converting formats, assembling and validating chains, inspecting certificates and generating keys — entirely on your own machine, with nothing uploaded anywhere.
>
> Unlike the online certificate converters, CertConvert never sends your files or private keys to a server. Everything happens locally.
>
> WHAT IT DOES
> • Inspect any certificate, key, CSR or bundle — subject, issuer, validity, SANs, key usage and fingerprints, plus a health check on every one.
> • Check whether a certificate covers a host name, and decode signing requests in full — the subject, key and extensions they request.
> • Convert between PEM, DER (.cer/.der), PKCS #7 (.p7b) and PKCS #12 (.pfx/.p12), in both directions.
> • Build chains — drop root, intermediate and device certificates in any order, have them ordered and validated automatically, then export as a PEM bundle, P7B or PFX.
> • Convert private-key formats (PKCS #8, PKCS #1, SEC 1, encrypted or not) and check whether a key matches a certificate.
> • Generate keys, certificate signing requests and self-signed certificates (RSA or ECDSA) with SANs and CA options.
> • Sign a certificate signing request with your own CA — choose a TLS server or client profile, set the validity and the subject alternative names and export either the issued certificate or the full chain. Requested extensions are never trusted; the result is always an end-entity certificate.
>
> BUILT FOR TRUST
> All cryptography uses the operating system's own libraries — there is no third-party cryptographic code. The app makes no network connections and collects no data. Private keys loaded from PKCS #12 files are handled in memory and are never imported into your keychain.
>
> Formats are detected from file content, not extensions, so misnamed files just work.

**What's New in 1.3.1:**
> A maintenance release: CertConvert is rebuilt on the latest .NET 10 servicing update, bringing security fixes to the cryptography and runtime libraries it relies on. No changes to features or behaviour.

## URLs

- **Marketing URL:** https://certconvert.com
- **Support URL:** https://certconvert.com/contact.html
- **Privacy Policy URL:** https://certconvert.com/privacy.html
- **Copyright:** 2026 Jermaine Walkes

## App privacy (nutrition label)

- **Data collection: None.** Select "Data Not Collected". The store build makes
  no network calls, has no analytics and no account.

## Export compliance — RESOLVED

`ITSAppUsesNonExemptEncryption = false` is set in the bundle Info.plist (via
make-mas-pkg.sh), so the per-submission encryption question is answered
automatically. Basis: CertConvert uses only standard, published algorithms
(RSA, ECDSA, PKCS) through the platform crypto libraries and implements no
proprietary encryption — qualifying for the exemption. Refs: Apple "Complying
with Encryption Export Regulations"; 15 CFR 740.17 / Category 5D002.

## App Review information

- Sign-in required: **No** (no account).
- Contact: Jermaine Walkes · support@certconvert.com · [your phone].
- **Review notes:**
  > CertConvert needs no account and no network access. To test: open the Generate tab, click Generate Key And Save, then Generate And Save Certificate — this produces a self-signed certificate with no external dependencies. The Inspect tab decodes any certificate file. All file access is through the standard open/save panels.

## Screenshots

Six 1280×800 PNGs (a valid App Store macOS size), store variant, in
`design/appstore-screenshots/`: 01-inspect, 02-convert, 03-chain, 04-keys,
05-generate, 08-sign. Regenerate with:
`CERTCONVERT_CAPTURE_DIR=design/appstore-screenshots dotnet test tests/CertConvert.App.Tests --filter StoreShots -p:StoreBuild=true`

## Decisions — all confirmed

- arm64-only: ship. · Price: £8.99 / $9.99. · Export compliance: exempt
  (`ITSAppUsesNonExemptEncryption=false`, baked into the pkg).

## Remaining steps (yours, after review)

1. Run the upload command above (or use Transporter) to send the build.
2. In App Store Connect: paste the text above, upload the screenshots, set
   pricing and privacy, attach the build, and **Submit for Review**.

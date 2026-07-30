# Test fixtures

Everything in this directory is disposable test material generated with OpenSSL 3.6
purely to verify interoperability. The private keys here protect nothing, are not
used anywhere and must never be. Password for all encrypted fixtures: `testpass`.

| File | What it is |
|---|---|
| `ossl_key.pem` | Unencrypted PKCS#8 RSA key (OpenSSL output) |
| `ossl_cert.pem` | Self-signed certificate for that key |
| `ossl_bundle.p7b` | DER PKCS#7 certs-only bundle (`crl2pkcs7`) |
| `ossl_modern.pfx` | PKCS#12, OpenSSL 3 defaults (AES-256/PBKDF2) |
| `ossl_3des.pfx` | PKCS#12 with SHA1-3DES PBE and SHA-1 MAC (legacy profile) |
| `legacy_rsa_des3.pem` | Traditional encrypted PEM, DES-EDE3-CBC + DEK-Info |
| `legacy_rsa_aes256.pem` | Traditional encrypted PEM, AES-256-CBC + DEK-Info |
| `pkcs8_encrypted_aes256.pem` | Encrypted PKCS#8 (OpenSSL 3 `genrsa -aes256` default) |
| `ossl_request.csr` | DER PKCS#10 request (EC P-256) with SANs, key usage and EKU, from OpenSSL — proves foreign-encoder extensions parse |
| `ossl_request_key.pem` | The unencrypted EC private key for `ossl_request.csr` (key↔request match test) |
| `weak_rsa1024_cert.pem` | Self-signed cert with a 1024-bit RSA key (health check: weak key) |
| `sha1_selfsigned_root.pem` | SHA-1 self-signed CA root (health check: weak signature *downgraded* on a root) |
| `sha1_cert.pem` | CA-signed leaf with a SHA-1 signature (health check: weak signature is Critical off a root) |
| `sha1_selfsigned_leaf.pem` | Self-signed **leaf** (CA:FALSE) with a SHA-1 signature (health check: weak signature stays Critical — only a self-signed *root* downgrades) |

The weak-key and SHA-1 fixtures are checked in on purpose: hardened OpenSSL and
Windows policy refuse to *generate* such material, and the CI gate runs in a Linux
container, so they must not be produced at test time. Loading and reading them is
always fine.

Regenerate with the commands in the git history if ever needed.

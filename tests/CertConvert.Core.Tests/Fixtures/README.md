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

Regenerate with the commands in the git history if ever needed.

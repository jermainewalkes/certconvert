CertConvert — sample files for App Review
=========================================

Fictional test data (example.com) so you can exercise every feature.
Nothing here is real or sensitive.

Password for the encrypted key and the PFX:  certconvert

Inspect tab — open any file:
  device-cert.pem / .cer / .p7b   a certificate in three formats
  device.pfx                      certificate + private key (password above)
  request.csr                     a certificate signing request
  device-key.pem                  a private key

Convert tab — load a certificate and export another format:
  e.g. open device-cert.pem, choose PKCS #12 (.pfx), set a password, save.

Chain tab — add these three (in any order), then Validate Chain:
  root-ca.pem, intermediate-ca.pem, server.pem
  (or open chain-bundle.pem, which contains all three shuffled)

Keys tab:
  Convert device-key.pem between formats, or use
  device-key-encrypted.pem (password above). Check Match with
  device-cert.pem to confirm the key belongs to the certificate.

Generate tab needs no input — it creates keys, CSRs and self-signed
certificates directly.
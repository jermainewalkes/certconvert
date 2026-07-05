# Windows smoke test — CertConvert 0.1.0 (win-x64)

Copy `artifacts/CertConvert-0.1.0-win-x64.zip` into the VM, unzip anywhere,
run `CertConvert.exe`. No .NET install needed. Expect SmartScreen's
"Windows protected your PC" on first run (unsigned build) — More Info → Run Anyway.

## GUI (~5 minutes)

- [ ] App launches; sidebar shows the shield icon, "CertConvert / Certificate Toolbox"
- [ ] Taskbar and window icon show the shield (not a generic exe icon)
- [ ] Generate: create a P-256 key (save it), CN `wintest.local`, tick CA,
      Save Self-Signed Certificate
- [ ] Inspect: drag the saved `.pem` in — fields render, SHA-256 shown,
      Copy PEM puts text on the clipboard; Clear resets the page
- [ ] Convert: drag cert in, target PKCS #12, add the key file, password `test`,
      Convert And Save — succeeds
- [ ] Inspect the new `.pfx` with password `test` — cert + key listed
- [ ] Wrong password on the `.pfx` — error names the file, app stays healthy
- [ ] Keys: open the key, Check Match against the cert — MATCH
- [ ] Dark/light: flip Windows theme in Settings; app follows without restart
- [ ] Keyboard only: Tab reaches the sidebar, arrows switch pages

## CLI (~2 minutes, from cmd or PowerShell in the unzip folder)

- [ ] `CertConvert.exe --version` prints 0.1.0+hash
- [ ] `CertConvert.exe --help` prints usage (GUI-subsystem console attach —
      output may interleave with the prompt; that's the documented quirk)
- [ ] `CertConvert.exe inspect <the .pem you made>` decodes it
- [ ] `CertConvert.exe chain verify <the .pem>` reports on it
- [ ] Double-clicking `CertConvert.exe` still opens the GUI (no console window)

## If anything fails

Note the step and any error text; screenshots welcome. `--version` output
identifies the exact build.

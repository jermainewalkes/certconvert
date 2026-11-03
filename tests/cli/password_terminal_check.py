#!/usr/bin/env python3
"""End-to-end check of the CLI's password input against the REAL binary.

The xunit tests (tests/CertConvert.App.Tests/CliPasswordInputTests.cs) cover the
rules with a fake console. This script covers what a fake cannot: that the hidden
prompt really reads from a terminal without echoing, that Ctrl+C leaves the
terminal usable, and that a piped password arrives through a real pipe.

macOS and Linux only (it needs a pseudo-terminal). Run from the repo root after a
build, passing the built executable:

    dotnet build src/CertConvert -c Debug
    DOTNET_ROOT=$HOME/.dotnet python3 tests/cli/password_terminal_check.py \
        src/CertConvert/bin/Debug/net10.0/CertConvert
"""
import codecs, os, select, signal, subprocess, sys, tempfile, time

if sys.platform == "win32":
    sys.exit("This check needs a pseudo-terminal, so it runs on macOS and Linux only. "
             "On Windows the prompt form is refused by design; see the README.")
if len(sys.argv) != 2 or not os.access(sys.argv[1], os.X_OK):
    sys.exit("usage: password_terminal_check.py <path to the built CertConvert executable>")
import fcntl, pty, termios

BIN = os.path.abspath(sys.argv[1])
SAMPLES = os.path.join(os.path.dirname(__file__), "..", "..", "site", "samples")
CERT, KEY, PFX = (os.path.join(SAMPLES, n) for n in ("device-cert.pem", "device-key.pem", "device.pfx"))
SAMPLE_PW = "certconvert"          # documented in site/samples/README.txt
NEW_PW = "typed-Pw 🔑 9"            # spaces and non-ASCII must survive

failures = []
def check(name, ok, detail=""):
    print(("  ok    " if ok else "  FAIL  ") + name + ("" if ok else "\n        " + detail))
    if not ok: failures.append(name)

def run(args, **kw):
    return subprocess.run([BIN, *args], capture_output=True, text=True, timeout=60, **kw)

def echo_on(*fds):
    """Whether the terminal echoes. Tries each end in turn: after the program exits,
    Linux can only answer through the end we kept open and macOS only through ours."""
    for fd in fds:
        try:
            return bool(termios.tcgetattr(fd)[3] & termios.ECHO)
        except (termios.error, OSError):
            continue
    raise RuntimeError("could not read the terminal settings from either end")

def read_available(fd, decode, wait):
    """Whatever the terminal has to show within `wait` seconds, or None at end of file."""
    ready, _, _ = select.select([fd], [], [], wait)
    if not ready: return ""
    try: chunk = os.read(fd, 4096)
    except OSError: return None
    return decode.decode(chunk) if chunk else None

def in_terminal(args, steps, interrupt=False):
    """Run the binary on a pseudo-terminal. steps: [(text to wait for, text to type)].

    Returns a dict: code (exit code), seen (everything the terminal displayed),
    prompts (how many of the awaited prompts appeared), echo_off_at_prompt (echo was
    off each time we typed), interrupted (Ctrl+C was actually sent) and echo_after.
    """
    # Open the terminal pair ourselves and keep our own handle on the program's end,
    # so its settings can still be read after the program has exited.
    fd, tty = pty.openpty()
    pid = os.fork()
    if pid == 0:
        try:
            os.close(fd)
            os.setsid()                                   # new session, no terminal yet
            fcntl.ioctl(tty, termios.TIOCSCTTY, 0)        # make this its controlling terminal
            for std in (0, 1, 2): os.dup2(tty, std)
            if tty > 2: os.close(tty)
            os.execv(BIN, [BIN, *args])
        finally:
            os._exit(127)                         # setup or exec failed: never fall into the parent's code
    decode = codecs.getincrementaldecoder("utf-8")("replace")   # characters may split across reads
    seen, searched, deadline, pending = "", 0, time.monotonic() + 60, list(steps)
    r = dict(code=None, prompts=0, echo_off_at_prompt=True, interrupted=False)

    def reap():
        done, status = os.waitpid(pid, os.WNOHANG)
        return os.waitstatus_to_exitcode(status) if done else None

    while time.monotonic() < deadline and r["code"] is None:
        more = read_available(fd, decode, 0.2)
        if more is None: break                    # the program closed the terminal
        seen += more
        # Look for the next prompt only in output that arrived after the previous one.
        at = seen.find(pending[0][0], searched) if pending else -1
        if at >= 0:
            label, typed = pending.pop(0)
            searched = at + len(label)
            r["prompts"] += 1
            # Wait for the program to switch echo off, not for a fixed time.
            waited = time.monotonic() + 5
            while echo_on(tty, fd) and time.monotonic() < waited:
                time.sleep(0.02)
            r["echo_off_at_prompt"] = r["echo_off_at_prompt"] and not echo_on(tty, fd)
            if interrupt:
                os.write(fd, b"\x03")             # typed, so the terminal raises the signal
                r["interrupted"] = True
            else:
                os.write(fd, typed.encode() + b"\r")
        r["code"] = reap()
    for _ in range(100):                          # give it up to 10s to finish exiting
        if r["code"] is not None: break
        time.sleep(0.1)
        r["code"] = reap()
    if r["code"] is None:
        os.kill(pid, signal.SIGKILL); os.waitpid(pid, 0); r["code"] = -999
    # Exit does not mean everything was read: drain what the terminal still holds.
    while True:
        more = read_available(fd, decode, 0.3)
        if not more: break
        seen += more
    r["echo_after"] = echo_on(tty, fd)
    r["seen"] = seen
    os.close(tty); os.close(fd)
    return r

with tempfile.TemporaryDirectory() as tmp:
    out = lambda n: os.path.join(tmp, n)

    # --- hidden prompt: a new password, typed twice, never shown ---
    t = in_terminal(
        ["convert", CERT, "--key", KEY, "-o", out("a.pfx"), "--out-password-prompt"],
        [("New password for the output: ", NEW_PW), ("Repeat the new password: ", NEW_PW)])
    check("prompt: new password asked for twice and accepted", t["code"] == 0 and t["prompts"] == 2, str(t))
    check("prompt: echo is off while the prompt waits", t["echo_off_at_prompt"], str(t))
    check("prompt: typed password is never displayed",
          not any(part in t["seen"] for part in ("typed-Pw", "🔑", NEW_PW)), repr(t["seen"]))
    check("prompt: terminal echo is back on afterwards", t["echo_after"], str(t))
    r = run(["inspect", out("a.pfx"), "--password", NEW_PW])
    check("prompt: output opens with exactly what was typed", r.returncode == 0 and "1 key" in r.stdout, r.stderr)

    # --- hidden prompt: an input password ---
    t = in_terminal(["inspect", PFX, "--password-prompt"], [("Password for the input: ", SAMPLE_PW)])
    check("prompt: input password opens the file", t["code"] == 0 and t["prompts"] == 1 and "device.example.com" in t["seen"], str(t["code"]))
    # The sample password is the lower-case word "certconvert"; the output only ever
    # contains the product name with capitals, so a case-sensitive search is exact.
    check("prompt: input password is never displayed", SAMPLE_PW not in t["seen"], repr(t["seen"][:200]))

    # --- editing keys: Backspace removes a character, Ctrl+U starts again ---
    t = in_terminal(["inspect", PFX, "--password-prompt"],
                    [("Password for the input: ", "wrong\x15" + SAMPLE_PW + "xy\x7f\x7f")])
    check("prompt: Ctrl+U clears and Backspace deletes", t["code"] == 0 and "device.example.com" in t["seen"], str(t["code"]))

    # --- mismatch writes nothing ---
    t = in_terminal(
        ["convert", CERT, "--key", KEY, "-o", out("b.pfx"), "--out-password-prompt"],
        [("New password for the output: ", "one"), ("Repeat the new password: ", "two")])
    check("prompt: mismatch fails with exit 2 and writes nothing",
          t["code"] == 2 and "did not match" in t["seen"] and not os.path.exists(out("b.pfx")), str(t))
    check("prompt: terminal echo is back on after a mismatch", t["echo_after"], str(t))

    # --- Ctrl+C at the prompt leaves the terminal echoing ---
    t = in_terminal(["inspect", PFX, "--password-prompt"], [("Password for the input: ", "")], interrupt=True)
    check("prompt: Ctrl+C was typed at a live, echo-off prompt",
          t["prompts"] == 1 and t["interrupted"] and t["echo_off_at_prompt"], str(t))
    check("prompt: Ctrl+C ends the program", t["code"] not in (0, 2, -999), str(t["code"]))
    check("prompt: Ctrl+C leaves terminal echo on", t["echo_after"], str(t))

    # --- a password "file" that is really the terminal, under any name, is refused ---
    for alias in ("/dev/tty", "/dev/./tty", "/dev/stdin"):
        t = in_terminal(["inspect", PFX, "--password-file", alias], [])
        check(f"file: {alias} on a terminal is refused, not read with echo on",
              t["code"] == 2 and "as you type" in t["seen"], str(t["code"]) + " " + repr(t["seen"][-200:]))

    # --- the other password families, through a real terminal ---
    t = in_terminal(["key", "match", "--cert", CERT, "--key", os.path.join(SAMPLES, "device-key-encrypted.pem"),
                     "--key-password-prompt"], [("Password for the key file: ", SAMPLE_PW)])
    check("prompt: --key-password-prompt unlocks the key file", t["code"] == 0 and "MATCH" in t["seen"], str(t))

    # --- no terminal: the prompt refuses instead of hanging ---
    r = run(["inspect", PFX, "--password-prompt"], stdin=subprocess.DEVNULL)
    check("prompt: refused when standard input is not a terminal",
          r.returncode == 2 and "standard input is redirected" in r.stderr, r.stderr)

    # --- a real pipe ---
    r = run(["inspect", PFX, "--password-file", "-"], input=SAMPLE_PW + "\n")
    check("pipe: password read from standard input", r.returncode == 0 and "device.example.com" in r.stdout, r.stderr)
    r = run(["inspect", PFX, "--password-file", "-"], input="")
    check("pipe: nothing piped is a clean failure", r.returncode == 2 and "No password arrived" in r.stderr, r.stderr)

    # --- file and environment through the real process ---
    pw_file = out("pw.txt")
    with open(pw_file, "w") as f: f.write(SAMPLE_PW + "\n")
    os.chmod(pw_file, 0o600)
    r = run(["inspect", PFX, "--password-file", pw_file])
    check("file: 0600 password file works without a warning", r.returncode == 0 and "Warning" not in r.stderr, r.stderr)
    os.chmod(pw_file, 0o644)
    r = run(["inspect", PFX, "--password-file", pw_file])
    check("file: a file others can read still works, with a warning",
          r.returncode == 0 and "can be read by other users" in r.stderr, r.stderr)
    r = subprocess.run(["bash", "-c", f'"{BIN}" inspect "{PFX}" --password-file <(printf %s {SAMPLE_PW})'],
                       capture_output=True, text=True, timeout=60)
    check("file: a process substitution works and draws no permission warning",
          r.returncode == 0 and "Warning" not in r.stderr, r.stderr)
    r = run(["inspect", PFX, "--password-env", "CC_TEST_PW"], env={**os.environ, "CC_TEST_PW": SAMPLE_PW})
    check("env: password read from the environment", r.returncode == 0, r.stderr)
    env = {k: v for k, v in os.environ.items() if k != "CC_TEST_PW"}
    r = run(["inspect", PFX, "--password-env", "CC_TEST_PW"], env=env)
    check("env: an unset variable is a clean failure", r.returncode == 2 and "is not set" in r.stderr, r.stderr)

    # --- unchanged behaviour ---
    r = run(["inspect", PFX, "--password", SAMPLE_PW])
    check("literal --password still works", r.returncode == 0, r.stderr)
    r = run(["inspect", PFX])
    check("no password: still fails at once, now naming the safer forms",
          r.returncode == 2 and "--password-prompt" in r.stderr and "--password-file" in r.stderr, r.stderr)
    r = run(["convert", CERT, "--key", KEY, "-o", out("c.pfx"), "--out-password-promt", "x"])
    check("misspelt password option is a usage error (exit 1) and writes nothing",
          r.returncode == 1 and "Unknown option --out-password-promt" in r.stderr and not os.path.exists(out("c.pfx")), r.stderr)
    r = run(["key", "convert", KEY, "-o", out("plain.key"), "--out-password", "x"])
    check("a new password that cannot be used is reported, not silently dropped",
          r.returncode == 0 and "was ignored" in r.stderr and "pkcs8-enc" in r.stderr, r.stderr)
    r = run(["inspect", PFX, "--password", SAMPLE_PW, "--password-env", "X"])
    check("two sources for one password is a usage error (exit 1)",
          r.returncode == 1 and "both supply the same password" in r.stderr, r.stderr)
    r = run(["--help"])
    check("--help documents the forms", "--password-prompt" in r.stdout and "--password-env" in r.stdout, "")

print(f"{'ALL PASSED' if not failures else str(len(failures)) + ' FAILED'}")
sys.exit(1 if failures else 0)

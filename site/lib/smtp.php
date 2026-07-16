<?php
/**
 * Minimal authenticated SMTP submission over STARTTLS. No external dependencies.
 *
 * Returns array{0:bool,1:string} -> [true, ''] on success, or [false, 'reason'] on failure.
 * The reason string is for server-side logging only; it is never shown to visitors.
 */

function cc_mime_encode(string $s): string
{
    // RFC 2047 encode any header value that contains non-ASCII characters.
    if (preg_match('/[^\x20-\x7E]/', $s)) {
        return '=?UTF-8?B?' . base64_encode($s) . '?=';
    }
    return $s;
}

function cc_display_name(string $s): string
{
    if (preg_match('/[^\x20-\x7E]/', $s)) {
        return cc_mime_encode($s);
    }
    // Quote the display name if it contains characters that are special in a header phrase.
    if (preg_match('/[",<>@()]/', $s)) {
        return '"' . str_replace('"', '', $s) . '"';
    }
    return $s;
}

function cc_smtp_send(array $cfg, string $fromEmail, string $fromName, string $toEmail, string $replyTo, string $subject, string $body): array
{
    $host = (string)($cfg['smtp_host'] ?? '');
    $port = (int)($cfg['smtp_port'] ?? 25);
    $user = (string)($cfg['smtp_user'] ?? '');
    $pass = (string)($cfg['smtp_pass'] ?? '');
    // On Fasthosts shared hosting the submission ports (587/465) are blocked and the
    // local mail relay on port 25 accepts mail to local domains without AUTH. Set
    // 'smtp_auth' => false in that case; leave it true (default) for authenticated submission.
    $auth = (bool)($cfg['smtp_auth'] ?? true);

    if ($host === '') {
        return [false, 'incomplete configuration'];
    }
    if ($auth && ($user === '' || $pass === '')) {
        return [false, 'authentication enabled but credentials are missing'];
    }

    $errno = 0;
    $errstr = '';
    $fp = @stream_socket_client("tcp://{$host}:{$port}", $errno, $errstr, 20);
    if (!$fp) {
        return [false, "connect failed: {$errstr} ({$errno})"];
    }
    stream_set_timeout($fp, 20);

    $read = function () use ($fp) {
        $data = '';
        while (($line = fgets($fp, 515)) !== false) {
            $data .= $line;
            // In a multiline reply each line has a '-' after the code; the final line has a space.
            if (isset($line[3]) && $line[3] === ' ') {
                break;
            }
        }
        return $data;
    };
    $cmd = function ($c) use ($fp, $read) {
        fwrite($fp, $c . "\r\n");
        return $read();
    };
    $expect = function ($resp, $codes) {
        $code = (int)substr($resp, 0, 3);
        return in_array($code, (array)$codes, true);
    };

    $resp = $read();
    if (!$expect($resp, 220)) { fclose($fp); return [false, "greeting: {$resp}"]; }

    $ehlo = 'EHLO certconvert.com';
    $resp = $cmd($ehlo);
    if (!$expect($resp, 250)) { fclose($fp); return [false, "ehlo: {$resp}"]; }
    $caps = strtoupper($resp);

    // Upgrade to TLS if the server advertises STARTTLS (encrypts the hop to the relay).
    if (strpos($caps, 'STARTTLS') !== false) {
        $resp = $cmd('STARTTLS');
        if (!$expect($resp, 220)) { fclose($fp); return [false, "starttls: {$resp}"]; }
        $crypto = STREAM_CRYPTO_METHOD_TLS_CLIENT;
        if (defined('STREAM_CRYPTO_METHOD_TLSv1_2_CLIENT')) {
            $crypto |= STREAM_CRYPTO_METHOD_TLSv1_2_CLIENT;
        }
        if (!stream_socket_enable_crypto($fp, true, $crypto)) {
            fclose($fp); return [false, 'tls negotiation failed'];
        }
        $resp = $cmd($ehlo);
        if (!$expect($resp, 250)) { fclose($fp); return [false, "ehlo(tls): {$resp}"]; }
        $caps = strtoupper($resp);
    }

    // Authenticate only when configured to (submission ports). The local relay on
    // port 25 does not offer AUTH and does not need it for local-domain delivery.
    if ($auth) {
        if (strpos($caps, 'AUTH') === false) { fclose($fp); return [false, 'server does not offer AUTH']; }
        $resp = $cmd('AUTH LOGIN');
        if (!$expect($resp, 334)) { fclose($fp); return [false, "auth: {$resp}"]; }
        $resp = $cmd(base64_encode($user));
        if (!$expect($resp, 334)) { fclose($fp); return [false, "auth user: {$resp}"]; }
        $resp = $cmd(base64_encode($pass));
        if (!$expect($resp, 235)) { fclose($fp); return [false, 'authentication failed']; }
    }

    // Envelope
    $resp = $cmd("MAIL FROM:<{$fromEmail}>");
    if (!$expect($resp, 250)) { fclose($fp); return [false, "mail from: {$resp}"]; }
    $resp = $cmd("RCPT TO:<{$toEmail}>");
    if (!$expect($resp, [250, 251])) { fclose($fp); return [false, "rcpt to: {$resp}"]; }
    $resp = $cmd('DATA');
    if (!$expect($resp, 354)) { fclose($fp); return [false, "data: {$resp}"]; }

    $headers = [];
    $headers[] = 'Date: ' . date('r');
    $headers[] = 'Message-ID: <' . bin2hex(random_bytes(16)) . '@certconvert.com>';
    $headers[] = 'From: ' . cc_display_name($fromName) . " <{$fromEmail}>";
    $headers[] = "To: <{$toEmail}>";
    if ($replyTo !== '') {
        $headers[] = "Reply-To: <{$replyTo}>";
    }
    $headers[] = 'Subject: ' . cc_mime_encode($subject);
    $headers[] = 'MIME-Version: 1.0';
    $headers[] = 'Content-Type: text/plain; charset=UTF-8';
    $headers[] = 'Content-Transfer-Encoding: 8bit';

    $data = implode("\r\n", $headers) . "\r\n\r\n" . $body;
    // Normalise to CRLF line endings and dot-stuff lines that begin with a dot.
    $data = preg_replace('/\r\n|\r|\n/', "\r\n", $data);
    $data = preg_replace('/^\./m', '..', $data);

    fwrite($fp, $data . "\r\n.\r\n");
    $resp = $read();
    if (!$expect($resp, 250)) { fclose($fp); return [false, "send: {$resp}"]; }

    $cmd('QUIT');
    fclose($fp);
    return [true, ''];
}

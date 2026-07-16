<?php
/**
 * Lightweight, dependency-free anti-spam helpers for the contact form.
 *
 *   - cc_client_ip()    : best-effort client IP for rate limiting.
 *   - cc_rate_ok()      : per-IP sliding-window rate limit (IPs stored hashed, in the
 *                         system temp dir, so no personal data is web-exposed).
 *   - cc_turnstile_ok() : verifies a Cloudflare Turnstile token. If no secret is
 *                         configured it returns true (the check is simply skipped),
 *                         so the form keeps working until Turnstile is set up.
 */

function cc_client_ip(): string
{
    return (string)($_SERVER['REMOTE_ADDR'] ?? '');
}

function cc_rate_ok(string $ip, int $max, int $windowSec): bool
{
    if ($ip === '') {
        return true; // can't identify the caller; don't block.
    }
    $file = sys_get_temp_dir() . '/cc_contact_rate.json';
    $fp = @fopen($file, 'c+');
    if (!$fp) {
        return true; // fail open: never block legitimate users on a storage hiccup.
    }
    flock($fp, LOCK_EX);
    $raw = stream_get_contents($fp);
    $data = json_decode($raw !== false && $raw !== '' ? $raw : '[]', true);
    if (!is_array($data)) {
        $data = [];
    }

    $now = time();
    $cut = $now - $windowSec;
    foreach ($data as $k => $times) {
        $kept = array_values(array_filter((array)$times, static function ($t) use ($cut) {
            return is_int($t) && $t > $cut;
        }));
        if ($kept) {
            $data[$k] = $kept;
        } else {
            unset($data[$k]);
        }
    }

    $key = hash('sha256', $ip); // store a hash, never the raw IP.
    $count = isset($data[$key]) ? count($data[$key]) : 0;
    $ok = $count < $max;
    if ($ok) {
        $data[$key][] = $now;
    }

    ftruncate($fp, 0);
    rewind($fp);
    fwrite($fp, json_encode($data));
    fflush($fp);
    flock($fp, LOCK_UN);
    fclose($fp);

    return $ok;
}

function cc_turnstile_ok(string $secret, string $response, string $ip): bool
{
    if ($secret === '') {
        return true; // not configured yet -> skip the check.
    }
    if ($response === '') {
        return false; // configured, but the visitor sent no token.
    }
    $ch = curl_init('https://challenges.cloudflare.com/turnstile/v0/siteverify');
    curl_setopt_array($ch, [
        CURLOPT_POST           => true,
        CURLOPT_POSTFIELDS     => http_build_query([
            'secret'   => $secret,
            'response' => $response,
            'remoteip' => $ip,
        ]),
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_TIMEOUT        => 10,
    ]);
    $res = curl_exec($ch);
    curl_close($ch);
    if ($res === false) {
        return false;
    }
    $j = json_decode($res, true);
    return is_array($j) && !empty($j['success']);
}

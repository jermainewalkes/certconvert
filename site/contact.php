<?php
/**
 * Contact-form handler for certconvert.com.
 *
 * Validates the submission, then sends it to the support mailbox via authenticated
 * SMTP. SMTP credentials live in mail.config.php (NOT in source control) -- copy
 * mail.config.sample.php to mail.config.php on the server and fill in the password.
 *
 * Uses the post/redirect/get pattern: on completion the browser is sent back to
 * contact.html with a ?sent / ?error flag that the page turns into a banner.
 */

require __DIR__ . '/lib/smtp.php';
require __DIR__ . '/lib/antispam.php';

$configFile = __DIR__ . '/mail.config.php';

function cc_redirect(string $query): void
{
    header('Location: contact.html?' . $query, true, 303);
    exit;
}

if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    cc_redirect('error=method');
}

// Honeypot: real users never see or fill the "company" field; bots do. Silently accept and drop.
if (!empty($_POST['company'])) {
    cc_redirect('sent=1');
}

// Per-IP rate limit: at most 5 submissions per hour from one address.
if (!cc_rate_ok(cc_client_ip(), 5, 3600)) {
    cc_redirect('error=rate');
}

if (!is_file($configFile)) {
    error_log('contact.php: mail.config.php not found');
    cc_redirect('error=config');
}
$cfg = require $configFile;

// Cloudflare Turnstile: enforced only when a secret is configured (see mail.config.php).
if (!cc_turnstile_ok((string)($cfg['turnstile_secret'] ?? ''), (string)($_POST['cf-turnstile-response'] ?? ''), cc_client_ip())) {
    cc_redirect('error=captcha');
}

$name    = trim((string)($_POST['name'] ?? ''));
$email   = trim((string)($_POST['email'] ?? ''));
$subject = trim((string)($_POST['subject'] ?? ''));
$message = trim((string)($_POST['message'] ?? ''));

if ($name === '' || $email === '' || $message === '') {
    cc_redirect('error=missing');
}
if (mb_strlen($name) > 100 || mb_strlen($email) > 150 || mb_strlen($subject) > 150 || mb_strlen($message) > 5000) {
    cc_redirect('error=length');
}
if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
    cc_redirect('error=email');
}
// Guard against header injection via the fields that end up in headers.
if (preg_match('/[\r\n]/', $name . $email . $subject)) {
    cc_redirect('error=invalid');
}

$subjectLine = $subject !== '' ? "[Contact] {$subject}" : "[Contact] Message from {$name}";

$body  = "New message from the CertConvert website contact form.\n\n";
$body .= "Name:    {$name}\n";
$body .= "Email:   {$email}\n";
if ($subject !== '') {
    $body .= "Subject: {$subject}\n";
}
$body .= "\nMessage:\n{$message}\n";

list($ok, $err) = cc_smtp_send(
    $cfg,
    (string)$cfg['mail_from'], // authenticated sender address
    $name,                     // display name shown in the inbox
    (string)$cfg['mail_to'],   // recipient (support mailbox)
    $email,                    // Reply-To = the visitor, so you can reply directly
    $subjectLine,
    $body
);

if (!$ok) {
    error_log('contact.php: send failed: ' . $err);
    cc_redirect('error=send');
}

cc_redirect('sent=1');

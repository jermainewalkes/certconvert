<?php
/**
 * SAMPLE mail configuration for the contact form.
 *
 * Copy this file to  mail.config.php  (same folder) and adjust as needed.
 *
 *   - mail.config.php is listed in .gitignore and must NEVER be committed.
 *   - It is plain PHP that only returns an array, so fetching it over HTTP yields a
 *     blank page (nothing is exposed in the response body).
 *   - The deploy script does not delete files that are absent locally.
 *
 * Fasthosts shared hosting blocks the outbound submission ports (587/465); its local
 * mail relay listens on port 25 and accepts mail to local domains WITHOUT AUTH. Hence
 * the live config uses port 25 with 'smtp_auth' => false and no password is required.
 * For an authenticated submission host instead, use port 587, 'smtp_auth' => true and a
 * real 'smtp_pass'.
 */

return [
    'smtp_host' => 'mailserver.livemail.co.uk',
    'smtp_port' => 25,                           // 25 = local relay (Fasthosts); 587 = submission
    'smtp_auth' => false,                        // false on the port-25 local relay; true for 587
    'smtp_user' => 'support@certconvert.com',    // mailbox login (only used when smtp_auth is true)
    'smtp_pass' => '',                           // only used when smtp_auth is true
    'mail_to'   => 'support@certconvert.com',    // where contact messages are delivered
    'mail_from' => 'support@certconvert.com',    // From address

    // Cloudflare Turnstile secret key — REQUIRED. The matching site key is baked
    // into contact.html, so the form fails closed (error=config) if this is
    // empty; verification also checks the token's hostname is certconvert.com.
    'turnstile_secret' => '',
];

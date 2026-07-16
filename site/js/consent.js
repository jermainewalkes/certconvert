/*
 * Cookie-consent banner for GA4 Consent Mode v2.
 *
 * Only ever runs when analytics is enabled — i.e. when analytics.js has set a
 * GA_MEASUREMENT_ID (exposed as window.CC_GA_ID). With no ID the site sets no
 * cookies at all, so there is nothing to consent to and no banner is shown.
 *
 * When active it:
 *   - re-grants analytics on later visits if the user previously accepted;
 *   - shows a banner (Accept / Decline) when no choice has been made yet;
 *   - exposes window.ccResetConsent() so a "change your choice" link can reopen it.
 *
 * The choice is stored in localStorage. No third-party consent service is used.
 */
(function () {
  // Wire the privacy page's "change your choice" link here (not inline in the
  // HTML) so the pages carry no inline script and CSP can stay strict. Wired
  // even when analytics is off, so the href="#" never jumps to the page top.
  function wireResetLink() {
    var link = document.getElementById("cc-reset-consent");
    if (link) {
      link.addEventListener("click", function (e) {
        e.preventDefault();
        if (window.ccResetConsent) { window.ccResetConsent(); }
      });
    }
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", wireResetLink);
  } else {
    wireResetLink();
  }

  if (!window.CC_GA_ID) return;   // analytics disabled -> no cookies -> no banner.

  var KEY = "cc_consent";

  function read() { try { return localStorage.getItem(KEY); } catch (e) { return null; } }
  function write(v) { try { localStorage.setItem(KEY, v); } catch (e) {} }

  function grant() {
    if (typeof window.gtag === "function") {
      window.gtag("consent", "update", { analytics_storage: "granted" });
    }
  }

  // Let visitors withdraw/change consent from a link anywhere on the site.
  window.ccResetConsent = function () {
    try { localStorage.removeItem(KEY); } catch (e) {}
    location.reload();
  };

  var choice = read();

  // Returning visitor who accepted: grant immediately (before the first hit).
  if (choice === "granted") { grant(); }

  // A decision already exists -> no banner.
  if (choice === "granted" || choice === "denied") { return; }

  function showBanner() {
    var style = document.createElement("style");
    style.textContent =
      ".cc-consent{position:fixed;left:0;right:0;bottom:0;z-index:9999;background:#14122e;color:#e7e9f2;" +
      "display:flex;flex-wrap:wrap;align-items:center;gap:14px;justify-content:center;" +
      "padding:14px 20px;box-shadow:0 -2px 14px rgba(0,0,0,.28);font-size:14px;line-height:1.5;}" +
      ".cc-consent-text{margin:0;max-width:760px;}" +
      ".cc-consent a{color:#a5b4fc;}" +
      ".cc-consent-actions{display:flex;gap:10px;flex-shrink:0;}" +
      ".cc-consent-btn{cursor:pointer;border:0;border-radius:8px;padding:9px 18px;font:inherit;font-weight:600;}" +
      ".cc-consent-accept{background:#4f46e5;color:#fff;}" +
      ".cc-consent-decline{background:transparent;color:#e7e9f2;border:1px solid #4b4e6a;}";
    document.head.appendChild(style);

    var bar = document.createElement("div");
    bar.className = "cc-consent";
    bar.setAttribute("role", "dialog");
    bar.setAttribute("aria-label", "Cookie consent");
    bar.innerHTML =
      '<p class="cc-consent-text">We use Google Analytics cookies to see how the site is used and improve it. ' +
      'Nothing is set unless you accept. See our <a href="privacy.html">privacy page</a>.</p>' +
      '<div class="cc-consent-actions">' +
      '<button type="button" class="cc-consent-btn cc-consent-decline">Decline</button>' +
      '<button type="button" class="cc-consent-btn cc-consent-accept">Accept</button>' +
      "</div>";
    document.body.appendChild(bar);

    function choose(v) {
      write(v);
      if (v === "granted") { grant(); }
      if (bar.parentNode) { bar.parentNode.removeChild(bar); }
    }
    bar.querySelector(".cc-consent-accept").addEventListener("click", function () { choose("granted"); });
    bar.querySelector(".cc-consent-decline").addEventListener("click", function () { choose("denied"); });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", showBanner);
  } else {
    showBanner();
  }
})();

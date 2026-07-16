/*
 * Google Analytics (GA4) loader + download-intent tracking for certconvert.com.
 *
 * GATED BY GA_MEASUREMENT_ID BELOW. While it is the empty string (the default),
 * NOTHING happens: no Google script is fetched, no cookies are set, and the
 * consent banner (consent.js) stays hidden. The site runs fully analytics-free.
 *
 * To enable analytics later: paste your GA4 Measurement ID (it looks like
 * "G-XXXXXXXXXX") into GA_MEASUREMENT_ID. That single change turns the whole
 * pipeline on — consent-mode defaults, the gtag.js loader and the banner.
 */
(function () {
  // ── Paste your GA4 Measurement ID here to enable analytics. Empty = off. ──
  var GA_MEASUREMENT_ID = "";

  // Expose the state so consent.js knows whether to show the cookie banner.
  window.CC_GA_ID = GA_MEASUREMENT_ID;
  if (!GA_MEASUREMENT_ID) return;   // no ID -> no analytics, no cookies, no banner.

  // Consent Mode v2: default everything to denied until the visitor accepts.
  window.dataLayer = window.dataLayer || [];
  function gtag() { dataLayer.push(arguments); }
  window.gtag = gtag;
  gtag("consent", "default", {
    ad_storage: "denied",
    ad_user_data: "denied",
    ad_personalization: "denied",
    analytics_storage: "denied",
    wait_for_update: 500
  });
  gtag("js", new Date());
  gtag("config", GA_MEASUREMENT_ID);

  // Load the gtag.js library (processes the queued commands above in order).
  var lib = document.createElement("script");
  lib.async = true;
  lib.src = "https://www.googletagmanager.com/gtag/js?id=" + encodeURIComponent(GA_MEASUREMENT_ID);
  document.head.appendChild(lib);

  // ── Download-intent tracking ──
  // Enhanced measurement records page views and outbound clicks already. These
  // two explicit events answer "where did people press download": the GitHub
  // releases link (no .zip suffix, so GA won't auto-detect it) and any in-site
  // Download call-to-action, tagged with the page it was clicked from.
  function currentPage() {
    var name = (location.pathname.split("/").pop() || "").replace(/\.html$/i, "");
    return name.length ? name : "home";
  }

  document.addEventListener("click", function (e) {
    var a = e.target && e.target.closest ? e.target.closest("a[href]") : null;
    if (!a || typeof window.gtag !== "function") return;

    var href = a.getAttribute("href") || "";
    var source = a.getAttribute("data-ga-source") || currentPage();
    var text = (a.textContent || "").replace(/\s+/g, " ").trim().slice(0, 60);

    if (/releases\/latest|releases\/download|\.zip(\?|#|$)/i.test(href)) {
      window.gtag("event", "file_download", {
        file_name: "CertConvert.zip",
        file_extension: "zip",
        link_url: a.href,
        link_text: text,
        source: source
      });
    } else if (/(^|\/)download\.html(\?|#|$)|^#get$/i.test(href)) {
      window.gtag("event", "download_cta", {
        link_text: text,
        source: source
      });
    }
  }, true);
})();

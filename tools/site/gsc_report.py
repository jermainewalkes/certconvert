"""Google Search Console report + sitemap submit for certconvert.com.

Reads the gsc-publisher Keychain JSON (client_id/secret/refresh_token) on
stdin. Usage:
  security find-generic-password -s gsc-publisher -w | python3 tools/site/gsc_report.py [--submit-sitemap]

Default: prints a search-performance summary (last 28 days) + top pages. With
--submit-sitemap it also (re)submits the sitemap. The gsc-publisher account must
have access to a verified certconvert.com Search Console property.
"""
import datetime
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

# Seconds any single connect or read may block. Not a total deadline: a server
# that keeps trickling data can run longer, but a stalled connection cannot hang the run.
TIMEOUT = 30

SITE = "https://certconvert.com/"
SITEMAP = "https://certconvert.com/sitemap.xml"
URLS = [
    "", "features.html", "download.html", "docs.html", "changelog.html",
    "donate.html", "contact.html", "privacy.html", "accessibility.html",
    "pem-to-pfx.html", "pfx-to-pem.html", "certificate-chain-order.html",
]


def token(creds):
    data = urllib.parse.urlencode({
        "client_id": creds["client_id"], "client_secret": creds["client_secret"],
        "refresh_token": creds["refresh_token"], "grant_type": "refresh_token"}).encode()
    return json.load(urllib.request.urlopen(
        urllib.request.Request("https://oauth2.googleapis.com/token", data=data),
        timeout=TIMEOUT))["access_token"]


def api(method, url, tok, body=None):
    req = urllib.request.Request(
        url, data=json.dumps(body).encode() if body else None, method=method,
        headers={"Authorization": "Bearer " + tok, "Content-Type": "application/json"})
    try:
        r = urllib.request.urlopen(req, timeout=TIMEOUT)
        return json.load(r) if r.status != 204 and r.length != 0 else {}
    except urllib.error.HTTPError as e:
        return {"_error": e.code, "detail": e.read().decode()[:300]}


def main():
    creds = json.loads(sys.stdin.read())
    tok = token(creds)

    sites = api("GET", "https://www.googleapis.com/webmasters/v3/sites", tok)
    if "_error" in sites:
        print(f"Search Console API error {sites['_error']}: {sites['detail']}")
        return
    props = [s["siteUrl"] for s in sites.get("siteEntry", [])]
    target = next((p for p in props if "certconvert.com" in p), None)
    print("Search Console properties:", props or "(none)")
    if not target:
        print("No certconvert.com property visible to this account — check the "
              "authed account owns it and the Search Console API is enabled.")
        return
    print("Using property:", target)

    if "--submit-sitemap" in sys.argv:
        q = urllib.parse.quote(target, safe="")
        sm = urllib.parse.quote(SITEMAP, safe="")
        r = api("PUT", f"https://www.googleapis.com/webmasters/v3/sites/{q}/sitemaps/{sm}", tok)
        print("Sitemap submit:", "OK" if "_error" not in r else r)

    # Search performance, last 28 days
    end = datetime.date.today()
    start = end - datetime.timedelta(days=28)
    q = urllib.parse.quote(target, safe="")
    perf = api("POST",
               f"https://www.googleapis.com/webmasters/v3/sites/{q}/searchAnalytics/query", tok,
               {"startDate": start.isoformat(), "endDate": end.isoformat(),
                "dimensions": ["query"], "rowLimit": 15})
    print(f"\nTop queries {start}..{end}:")
    rows = perf.get("rows", [])
    if not rows:
        print("  (no impressions yet — normal for a new site; check back in a few weeks)")
    for r in rows:
        print(f"  {r['keys'][0][:45]:45} clicks={int(r['clicks'])} impr={int(r['impressions'])} "
              f"ctr={r['ctr']*100:.1f}% pos={r['position']:.1f}")

    pages = api("POST",
                f"https://www.googleapis.com/webmasters/v3/sites/{q}/searchAnalytics/query", tok,
                {"startDate": start.isoformat(), "endDate": end.isoformat(),
                 "dimensions": ["page"], "rowLimit": 15})
    print("\nTop pages:")
    for r in pages.get("rows", []):
        print(f"  {r['keys'][0].replace('https://certconvert.com/', '/'):40} "
              f"clicks={int(r['clicks'])} impr={int(r['impressions'])}")


if __name__ == "__main__":
    try:
        main()
    except (urllib.error.URLError, TimeoutError) as e:
        # A stalled or refused connection: fail fast with one line so the caller
        # can retry, instead of hanging with no output.
        sys.exit(f"Network error: {e}")

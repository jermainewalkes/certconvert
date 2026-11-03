"""Bing Webmaster Tools report for certconvert.com.

Reads the Bing Webmaster API key (plain string) on stdin. Usage:
  security find-generic-password -s bing-webmaster -w | python3 tools/site/bing_report.py [--submit-urls]

Prints crawl/traffic stats (impressions, clicks) and top queries. With
--submit-urls it also pushes the key pages for (re)crawling — cheap against
the ~10k/day quota, useful after content changes. certconvert.com must be
verified on this Bing Webmaster key.
"""
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

# Seconds any single connect or read may block. Not a total deadline: a server
# that keeps trickling data can run longer, but a stalled connection cannot hang the run.
TIMEOUT = 30

SITE = "https://certconvert.com"
KEY_PAGES = [
    "/", "features.html", "download.html", "docs.html", "changelog.html",
    "donate.html", "contact.html", "privacy.html", "accessibility.html",
    "pem-to-pfx.html", "pfx-to-pem.html", "certificate-chain-order.html",
]
BASE = "https://ssl.bing.com/webmaster/api.svc/json"


def api_get(method, key, **params):
    q = urllib.parse.urlencode({"apikey": key, **params})
    try:
        return json.load(urllib.request.urlopen(f"{BASE}/{method}?{q}", timeout=TIMEOUT))
    except urllib.error.HTTPError as e:
        return {"_error": e.code, "detail": e.read().decode()[:200]}


def api_post(method, key, body):
    req = urllib.request.Request(f"{BASE}/{method}?apikey={key}",
        data=json.dumps(body).encode(), method="POST",
        headers={"Content-Type": "application/json; charset=utf-8"})
    try:
        r = urllib.request.urlopen(req, timeout=TIMEOUT)
        return json.load(r) if r.length else {"ok": True}
    except urllib.error.HTTPError as e:
        return {"_error": e.code, "detail": e.read().decode()[:200]}


def main():
    key = sys.stdin.read().strip()

    # confirm the site is verified
    sites = api_get("GetUserSites", key)
    urls = [s.get("Url") for s in sites.get("d", [])] if isinstance(sites.get("d"), list) else []
    verified = any("certconvert.com" in (u or "") for u in urls)
    print(f"Bing: certconvert.com verified = {verified}")
    if not verified:
        print("  (site not verified on this key — add it in Bing Webmaster)"); return

    if "--submit-urls" in sys.argv:
        r = api_post("SubmitUrlbatch", key,
                     {"siteUrl": SITE, "urlList": [SITE + ("" if p == "/" else "/" + p) for p in KEY_PAGES]})
        q = api_get("GetUrlSubmissionQuota", key, siteUrl=SITE)
        daily = q.get("d", {}).get("DailyQuota", "?")
        print(f"  submitted {len(KEY_PAGES)} URLs for crawl ({'ok' if '_error' not in r else r}); daily quota left: {daily}")

    # traffic stats (impressions/clicks over the reported period)
    stats = api_get("GetRankAndTrafficStats", key, siteUrl=SITE)
    rows = stats.get("d", []) if isinstance(stats.get("d"), list) else []
    if rows:
        imp = sum(int(r.get("Impressions", 0)) for r in rows)
        clk = sum(int(r.get("Clicks", 0)) for r in rows)
        print(f"  traffic (reported window): {imp} impressions, {clk} clicks")
    else:
        print("  traffic: no data yet (Bing takes a while to accrue for new sites)")

    # top queries
    q = api_get("GetQueryStats", key, siteUrl=SITE)
    qrows = q.get("d", []) if isinstance(q.get("d"), list) else []
    if qrows:
        print("  top Bing queries:")
        for r in sorted(qrows, key=lambda x: int(x.get("Impressions", 0)), reverse=True)[:8]:
            print(f"    {r.get('Query', '?')[:40]:40} impr={r.get('Impressions')} clk={r.get('Clicks')}")


if __name__ == "__main__":
    try:
        main()
    except (urllib.error.URLError, TimeoutError) as e:
        # A stalled or refused connection: fail fast with one line so the caller
        # can retry, instead of hanging with no output.
        sys.exit(f"Network error: {e}")

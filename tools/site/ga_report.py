"""Google Analytics 4 usage report for certconvert.com.

Reads the ga-analytics Keychain JSON (client_id/secret/refresh_token) on
stdin. Usage:
  security find-generic-password -s ga-analytics -w | python3 tools/site/ga_report.py [--days N]

Answers "is the site being found and driving people to the stores?" — active
users, sessions, page views, the download-intent events (file_download,
download_cta) and outbound click-throughs by destination (Mac App Store,
Microsoft Store, GitHub, Ko-fi) for the CertConvert GA4 property (measurement id
G-66Q02ES3BN). Default 30 days.
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

MEASUREMENT_ID = "G-66Q02ES3BN"
STORE_DOMAINS = ("apps.apple.com", "apps.microsoft.com")


def token(creds):
    data = urllib.parse.urlencode({
        "client_id": creds["client_id"], "client_secret": creds["client_secret"],
        "refresh_token": creds["refresh_token"], "grant_type": "refresh_token"}).encode()
    return json.load(urllib.request.urlopen(
        urllib.request.Request("https://oauth2.googleapis.com/token", data=data),
        timeout=TIMEOUT))["access_token"]


def get(url, tok):
    req = urllib.request.Request(url, headers={"Authorization": "Bearer " + tok})
    return json.load(urllib.request.urlopen(req, timeout=TIMEOUT))


def post(url, tok, body):
    req = urllib.request.Request(url, data=json.dumps(body).encode(), method="POST",
        headers={"Authorization": "Bearer " + tok, "Content-Type": "application/json"})
    try:
        return json.load(urllib.request.urlopen(req, timeout=TIMEOUT))
    except urllib.error.HTTPError as e:
        return {"_error": e.code, "detail": e.read().decode()[:400]}


def find_property(tok):
    """Locate the numeric GA4 property whose data stream carries MEASUREMENT_ID."""
    summ = get("https://analyticsadmin.googleapis.com/v1beta/accountSummaries", tok)
    for acc in summ.get("accountSummaries", []):
        for ps in acc.get("propertySummaries", []):
            pid = ps["property"].split("/")[-1]
            streams = get(f"https://analyticsadmin.googleapis.com/v1beta/properties/{pid}/dataStreams", tok)
            for st in streams.get("dataStreams", []):
                mid = st.get("webStreamData", {}).get("measurementId")
                if mid == MEASUREMENT_ID:
                    return pid, ps.get("displayName")
    return None, None


def run_report(pid, tok, start, dims, mets, limit=15, order_metric=None, dim_filter=None):
    body = {"dateRanges": [{"startDate": start, "endDate": "today"}],
            "dimensions": [{"name": d} for d in dims],
            "metrics": [{"name": m} for m in mets],
            "limit": limit}
    if order_metric:
        body["orderBys"] = [{"metric": {"metricName": order_metric}, "desc": True}]
    if dim_filter:
        body["dimensionFilter"] = dim_filter
    return post(f"https://analyticsdata.googleapis.com/v1beta/properties/{pid}:runReport", tok, body)


def rows(rep):
    out = []
    for r in rep.get("rows", []):
        out.append(([d["value"] for d in r.get("dimensionValues", [])],
                    [m["value"] for m in r.get("metricValues", [])]))
    return out


def main():
    days = 30
    if "--days" in sys.argv:
        days = int(sys.argv[sys.argv.index("--days") + 1])
    start = (datetime.date.today() - datetime.timedelta(days=days)).isoformat()

    creds = json.loads(sys.stdin.read())
    tok = token(creds)

    pid, name = find_property(tok)
    if not pid:
        print(f"No GA4 property found for {MEASUREMENT_ID} on this account.")
        return
    print(f"Property: {name} ({pid}) — last {days} days (from {start})\n")

    # headline
    tot = run_report(pid, tok, start, [], ["activeUsers", "sessions", "screenPageViews", "eventCount"])
    if "_error" in tot:
        print("Data API error:", tot); return
    r = rows(tot)
    if r:
        u, s, pv, ev = r[0][1]
        print(f"Active users: {u} | Sessions: {s} | Pageviews: {pv} | Total events: {ev}")
    else:
        print("No data yet in this window.")

    # the answer: download intent
    print("\n— Download intent —")
    ev_by_name = run_report(pid, tok, start, ["eventName"], ["eventCount"], limit=50)
    counts = {d[0]: int(m[0]) for d, m in rows(ev_by_name)}
    for name_ in ("file_download", "download_cta", "click"):
        if name_ in counts:
            print(f"  {name_:14} {counts[name_]}")
    if not (counts.get("file_download") or counts.get("download_cta")):
        print("  (no download events yet — either no download intent, or visitors declined analytics consent)")

    # outbound click-throughs by destination (store links, GitHub, Ko-fi)
    print("\n— Outbound click-throughs (store / download destinations) —")
    ob = run_report(pid, tok, start, ["linkDomain"], ["eventCount"], limit=15, order_metric="eventCount",
                    dim_filter={"filter": {"fieldName": "eventName",
                                           "stringFilter": {"value": "click"}}})
    store_clicks = 0
    obrows = rows(ob)
    if not obrows:
        print("  (no outbound clicks recorded yet)")
    for dims, mets in obrows:
        dom, c = dims[0], int(mets[0])
        if dom in STORE_DOMAINS:
            store_clicks += c
        print(f"  {dom:30} {c}")
    print(f"  → store click-throughs (App Store + Microsoft Store): {store_clicks}")

    # acquisition
    print("\n— Where visitors came from —")
    src = run_report(pid, tok, start, ["sessionSource", "sessionMedium"], ["sessions"],
                     limit=10, order_metric="sessions")
    for dims, mets in rows(src):
        print(f"  {dims[0]}/{dims[1]:12} {mets[0]} sessions")

    # top pages
    print("\n— Top pages —")
    pg = run_report(pid, tok, start, ["pagePath"], ["screenPageViews"], limit=10, order_metric="screenPageViews")
    for dims, mets in rows(pg):
        print(f"  {dims[0]:34} {mets[0]}")


if __name__ == "__main__":
    try:
        main()
    except (urllib.error.URLError, TimeoutError) as e:
        # A stalled or refused connection: fail fast with one line so the caller
        # can retry, instead of hanging with no output.
        sys.exit(f"Network error: {e}")

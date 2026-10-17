"""Formats CounterShift API responses into one readable line each, for load/demo.sh.

    curl ... | python load/demo_format.py decision|buckets|mode|version
"""
import json
import sys

kind = sys.argv[1]
raw = sys.stdin.read()

if kind == "decision":
    # demo.sh appends "\n<status> <seconds>" to the body with curl -w.
    body, status = raw.rsplit("\n", 1)
    code, seconds = status.split()
    data = json.loads(body)
    if code != "200":
        print(f"  HTTP {code}  {data.get('title')}")
    else:
        r1 = next(r for r in data["rules"] if r["id"] == "R1")
        print(f"  HTTP 200  decision={data['decision']:<9} R1: {r1['count']} failed payments in 10 min "
              f"(limit {r1['threshold']})  {float(seconds) * 1000:.1f} ms")
elif kind == "buckets":
    data = json.loads(raw)
    parts = "  ".join(f"{b['granularity']}@{b['start'][11:16]}={b['count']}" for b in data["buckets"])
    print(f"  count={data['count']}  window from {data['effectiveWindow']['from'][11:19]}Z  buckets: {parts}")
elif kind == "mode":
    data = json.loads(raw)
    p = data["parity"]
    print(f"  v{data['version']}  {data['summary']}   "
          f"parity: match={p['match']} mismatch={p['mismatch']} race={p['race']}")
elif kind == "version":
    print(json.loads(raw)["version"])

"""Local migration rehearsal under load (M8).

Drives the whole plan from docs/runbooks/migration.md by editing the storage-mode file while k6 sends
traffic, then asks Prometheus what happened in each step. Every number in the report comes from either
k6's own summary or a PromQL query printed next to it.

Prerequisites (see docs/learning/08-load-and-rehearsal.md):
  docker compose -f deploy/compose/docker-compose.yml up -d --wait
  API running in Release with COUNTERSHIFT_STORAGE_MODE_FILE=<mode file> and OTLP export to the local Prometheus

  python load/rehearse.py --mode-file <same mode file> [--step-seconds 30] [--event-rate 200] [--decision-rate 400]

Why two key prefixes: the backfill's second pass needs a full day (ADR 0012), which a 10-minute rehearsal
can't wait for. So Phase 0 traffic uses prefix A; after T0 all traffic uses fresh prefix B, whose history is
complete in both stores through dual writes. Backfill correctness itself is proven by the M6 tests (fake clock),
and the real multi-day migration happens on the server (M10).
"""
import argparse
import datetime as dt
import json
import os
import pathlib
import platform
import subprocess
import time
import urllib.parse
import urllib.request

import sys

# Mode summaries contain "→". On Windows, redirected output defaults to the cp1252 code page, which can't encode it.
sys.stdout.reconfigure(encoding="utf-8")

REPO = pathlib.Path(__file__).resolve().parent.parent
RESULTS = REPO / "load" / "results"


def get_json(url):
    with urllib.request.urlopen(url, timeout=10) as r:
        return json.load(r)


def mode_json(write, read):
    w = {"Mode": write[0], "Primary": write[1], **({"Secondary": write[2]} if len(write) > 2 else {})}
    r = {"Mode": read[0], "Primary": read[1], **({"Secondary": read[2], "Percent": read[3]} if len(read) > 2 else {})}
    return {"CounterShift": {"Storage": {"Write": w, "Read": r}}}


class Rehearsal:
    def __init__(self, args):
        self.a = args
        self.windows = []   # (name, summary, start, end) for every measured step
        self.containers = []

    # ── API helpers ────────────────────────────────────────────────────────────────────────────────
    def admin(self):
        return get_json(f"{self.a.api}/admin/storage-mode")

    @staticmethod
    def matches(state, write, read):
        w, r = state["write"], state["read"]
        return (w["mode"].lower(), w["primary"], w.get("secondary")) == (write[0].lower(), write[1], write[2] if len(write) > 2 else None) and             (r["mode"].lower(), r["primary"], r.get("secondary"), float(r.get("percent") or 0)) ==             (read[0].lower(), read[1], read[2] if len(read) > 2 else None, float(read[3]) if len(read) > 3 else 0.0)

    def set_mode(self, write, read):
        """Writes the mode file and returns the propagation time: file written → API reports the new version."""
        current = self.admin()
        if self.matches(current, write, read):
            return None, current  # already active: the API ignores no-op changes, so there is no new version to wait for
        before = current["version"]
        pathlib.Path(self.a.mode_file).write_text(json.dumps(mode_json(write, read)), encoding="utf-8")
        written = time.perf_counter()
        while time.perf_counter() - written < 120:
            state = self.admin()
            if state["version"] > before:
                return (time.perf_counter() - written) * 1000, state
            time.sleep(0.05)
        raise RuntimeError(f"mode change not applied within 120 s: {write} {read}")

    def hold(self, name, seconds, propagation_ms=None):
        summary = self.admin()["summary"]
        start = time.time()
        print(f"  {name:<34} {summary}  ({seconds}s)", flush=True)
        time.sleep(seconds)
        self.windows.append({"name": name, "mode": summary, "start": start, "end": time.time(),
                             "seconds": seconds, "propagation_ms": propagation_ms})

    def step(self, name, write, read, seconds):
        ms, _ = self.set_mode(write, read)
        self.hold(name, seconds, ms)

    # ── k6 ─────────────────────────────────────────────────────────────────────────────────────────
    def k6(self, prefix, duration_s):
        # Named, so cleanup can remove it. Killing `docker run` only kills the client: the container keeps
        # sending load. One orphaned k6 doubled the load of an entire rehearsal (learning note 08).
        name = f"cs-k6-{prefix}"
        self.containers.append(name)
        cmd = ["docker", "run", "--rm", "-i", "--name", name, "-v", f"{REPO / 'load'}:/load",
               "-e", f"BASE_URL={self.a.k6_base}", "-e", f"PREFIX={prefix}", "-e", f"DURATION={duration_s}s",
               "-e", f"EVENT_RATE={self.a.event_rate}", "-e", f"DECISION_RATE={self.a.decision_rate}",
               "grafana/k6:2.3.0", "run", "--quiet", "/load/traffic.js"]
        env = {**os.environ, "MSYS_NO_PATHCONV": "1"}
        return subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def k6_summary(self, prefix):
        data = json.loads((RESULTS / f"summary-{prefix}.json").read_text(encoding="utf-8"))
        m = data["metrics"]
        def trend(name):
            v = m[name]["values"]
            return {k: round(v[k], 2) for k in ("p(50)", "p(95)", "p(99)", "max")}
        return {"requests": m["http_reqs"]["values"]["count"], "failed_rate": m["http_req_failed"]["values"]["rate"],
                "failed": m["http_req_failed"]["values"].get("passes", 0),
                "dropped_iterations": m.get("dropped_iterations", {}).get("values", {}).get("count", 0),
                "latency_ms": trend("http_req_duration")}

    # ── Prometheus ─────────────────────────────────────────────────────────────────────────────────
    def prom(self, query, at):
        url = f"{self.a.prom}/api/v1/query?" + urllib.parse.urlencode({"query": query, "time": at})
        return get_json(url)["data"]["result"]

    def measure(self, w):
        W = f"{int(w['seconds'])}s"
        at = w["end"]
        def scalar(q):
            r = self.prom(q, at)
            return float(r[0]["value"][1]) if r else 0.0
        def by(q, labels):
            return {" ".join(r["metric"].get(l, "") for l in labels).strip(): float(r["value"][1])
                    for r in self.prom(q, at) if r["value"][1] not in ("NaN",)}
        h = "http_server_request_duration_seconds"
        s = "cs_storage_operation_duration_seconds"
        w["rps"] = by(f"sum by (http_route) (increase({h}_count[{W}])) / {w['seconds']}", ["http_route"])
        w["non2xx"] = scalar(f'sum(increase({h}_count{{http_response_status_code!~"2.."}}[{W}])) or vector(0)')
        for q in ("0.5", "0.99"):
            w[f"http_p{q}"] = by(f"histogram_quantile({q}, sum by (le, http_route) (increase({h}_bucket[{W}])))", ["http_route"])
            w[f"storage_p{q}"] = by(f"histogram_quantile({q}, sum by (le, backend, op, role) (increase({s}_bucket[{W}])))", ["backend", "op", "role"])
        w["parity"] = by(f"sum by (result) (increase(cs_parity_comparisons_total[{W}]))", ["result"])
        w["secondary_failures"] = scalar(f"sum(increase(cs_storage_secondary_write_failures_total[{W}])) or vector(0)")
        w["shadow_dropped"] = scalar(f"sum(increase(cs_parity_shadow_dropped_total[{W}])) or vector(0)")

    # ── The plan ───────────────────────────────────────────────────────────────────────────────────
    def run(self):
        running = subprocess.run(["docker", "ps", "-q", "--filter", "ancestor=grafana/k6:2.3.0"], capture_output=True, text=True).stdout.split()
        if running:
            raise SystemExit(f"Refusing to start: {len(running)} k6 container(s) already running would add load. Stop them first.")
        try:
            self._run()
        finally:
            for name in self.containers:
                subprocess.run(["docker", "rm", "-f", name], capture_output=True)

    def _run(self):
        RESULTS.mkdir(parents=True, exist_ok=True)
        S = self.a.step_seconds
        run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        pg, rd = "postgres", "redis"

        print("Phase 0: baseline on postgres, key prefix A")
        self.set_mode(("Single", pg), ("Single", pg))
        k6a = self.k6(f"{run_id}A", 2 * S + 10)
        time.sleep(10)  # warm-up: connection pools, JIT, first-request costs
        self.hold("P0 baseline", 2 * S)
        k6a.wait()

        print("Phase 1: dual writes. T0 = now. Fresh key prefix B from here on.")
        plan = [  # (name, write, read, seconds)
            ("P2 shadow 5%", ("Dual", pg, rd), ("Shadow", pg, rd, 5), S),
            ("P2 shadow 20%", ("Dual", pg, rd), ("Shadow", pg, rd, 20), S),
            ("P2 shadow 50%", ("Dual", pg, rd), ("Shadow", pg, rd, 50), S),
            ("P2 shadow 100%", ("Dual", pg, rd), ("Shadow", pg, rd, 100), 2 * S),
            ("P3 split 5%", ("Dual", pg, rd), ("Split", pg, rd, 5), S),
            ("P3 split 30%", ("Dual", pg, rd), ("Split", pg, rd, 30), S),
            ("P3 split 70%", ("Dual", pg, rd), ("Split", pg, rd, 70), S),
            ("ROLLBACK to postgres reads", ("Dual", pg, rd), ("Single", pg), S),
            ("P3 split 70% (again)", ("Dual", pg, rd), ("Split", pg, rd, 70), S),
            ("P3 split 100%", ("Dual", pg, rd), ("Split", pg, rd, 100), S),
            ("P4 redis primary, pg dual", ("Dual", rd, pg), ("Single", rd), 2 * S),
            ("P5 redis only", ("Single", rd), ("Single", rd), 2 * S),
        ]
        total = 2 * S + 25 + sum(p[3] for p in plan) + 15
        ms, state = self.set_mode(("Dual", pg, rd), ("Single", pg))
        t0 = state["loadedAt"]
        k6b = self.k6(f"{run_id}B", total)
        time.sleep(10)
        self.hold("P1 dual writes", 2 * S, ms)

        print("Backfill (command demo; see the docstring for why history isn't exercised here)")
        out = subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--project", str(REPO / "src/CounterShift.Api"),
                              "--no-launch-profile", "--", "backfill", "--from", pg, "--to", rd, "--dual-write-since", t0],
                             env={**os.environ, "COUNTERSHIFT_STORAGE_MODE_FILE": self.a.mode_file, "ASPNETCORE_ENVIRONMENT": "Development"},
                             capture_output=True, text=True, timeout=300)
        backfill = "\n".join(l for l in out.stdout.splitlines() if l and not l.startswith(("info", "warn", "      ")))
        print(backfill)

        for name, write, read, seconds in plan:
            self.step(name, write, read, seconds)

        print("waiting for k6 to finish…")
        k6b.wait()
        time.sleep(12)  # let the last metrics export reach Prometheus
        for w in self.windows:
            self.measure(w)

        report = {"run_id": run_id, "t0": t0, "machine": machine(), "args": vars(self.a), "backfill": backfill,
                  "k6_baseline": self.k6_summary(f"{run_id}A"), "k6_migration": self.k6_summary(f"{run_id}B"),
                  "steps": self.windows, "final_parity": self.admin()["parity"]}
        (RESULTS / f"rehearsal-{run_id}.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        md = render(report)
        (RESULTS / f"rehearsal-{run_id}.md").write_text(md, encoding="utf-8")
        print(md)


def machine():
    return {"os": platform.platform(), "cpus": os.cpu_count(), "python": platform.python_version()}


def ms(x):
    return "" if x is None else f"{x * 1000:.1f}"


def render(r):
    lines = [f"# Migration rehearsal {r['run_id']}", "",
             f"- Machine: {r['machine']['os']}, {r['machine']['cpus']} logical CPUs (API, PostgreSQL, Redis, Prometheus and k6 all on this one machine)",
             f"- Load: {r['args']['event_rate']} events/s + {r['args']['decision_rate']} decisions/s, open model (k6 constant arrival rate), 30% of traffic on 1% of cards",
             f"- T0 (dual writes on): {r['t0']}", ""]
    for label, k in (("Phase 0 baseline run", "k6_baseline"), ("Migration run (P1→P5)", "k6_migration")):
        s = r[k]
        lines.append(f"**k6, {label}:** {s['requests']:,} requests, **failed rate {s['failed_rate']:.5f}**, "
                     f"dropped iterations {s['dropped_iterations']}, client latency p50/p95/p99 = "
                     f"{s['latency_ms']['p(50)']}/{s['latency_ms']['p(95)']}/{s['latency_ms']['p(99)']} ms")
        lines.append("")
    lines += ["## Per step: traffic, errors, HTTP latency (server side, from Prometheus)", "",
              "| Step | Mode | Propagation (ms) | req/s events | req/s decisions | non-2xx | events p50 / p99 (ms) | decisions p50 / p99 (ms) |",
              "|---|---|---:|---:|---:|---:|---:|---:|"]
    for w in r["steps"]:
        ev, de = "/v1/events", "/v1/decisions"
        lines.append(f"| {w['name']} | `{w['mode']}` | {'' if w['propagation_ms'] is None else round(w['propagation_ms'])} | "
                     f"{w['rps'].get(ev, 0):.0f} | {w['rps'].get(de, 0):.0f} | {w['non2xx']:.0f} | "
                     f"{ms(w['http_p0.5'].get(ev))} / {ms(w['http_p0.99'].get(ev))} | {ms(w['http_p0.5'].get(de))} / {ms(w['http_p0.99'].get(de))} |")
    combos = sorted({k for w in r["steps"] for k in w["storage_p0.99"]})
    lines += ["", "## Per step: storage p99 by backend / op / role (ms)", "",
              "| Step | " + " | ".join(combos) + " |", "|---|" + "---:|" * len(combos)]
    for w in r["steps"]:
        lines.append(f"| {w['name']} | " + " | ".join(ms(w["storage_p0.99"].get(c)) for c in combos) + " |")
    lines += ["", "## Per step: parity and failure counters", "",
              "| Step | match | mismatch | race | error | shadow dropped | secondary write failures |", "|---|---:|---:|---:|---:|---:|---:|"]
    for w in r["steps"]:
        p = w["parity"]
        lines.append(f"| {w['name']} | {p.get('match', 0):.0f} | {p.get('mismatch', 0):.0f} | {p.get('race', 0):.0f} | {p.get('error', 0):.0f} | "
                     f"{w['shadow_dropped']:.0f} | {w['secondary_failures']:.0f} |")
    lines += ["", f"Final parity totals from /admin/storage-mode: `{json.dumps(r['final_parity'])}`", "",
              "## Backfill command output", "", "```", r["backfill"], "```", "",
              "Notes: Prometheus `increase()` over a step window is approximate (samples every 5 s, extrapolated at window edges). "
              "Server-side latency excludes the network and k6; the k6 numbers include both.", ""]
    return "\n".join(lines)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--mode-file", required=True)
    p.add_argument("--api", default="http://127.0.0.1:5072")
    p.add_argument("--prom", default="http://127.0.0.1:59090")
    p.add_argument("--k6-base", default="http://host.docker.internal:5072")
    p.add_argument("--step-seconds", type=int, default=30)
    p.add_argument("--event-rate", type=int, default=200)
    p.add_argument("--decision-rate", type=int, default=400)
    Rehearsal(p.parse_args()).run()

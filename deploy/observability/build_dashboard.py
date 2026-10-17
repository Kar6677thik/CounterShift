"""Generates grafana/dashboards/countershift.json. Dashboards as code: edit this file, re-run it, commit both.

    python deploy/observability/build_dashboard.py

Each panel is one PromQL question. The comments say what the question is, which is the part worth learning.
Metric names are the OTLP → Prometheus translation of the OpenTelemetry names in the code:
    cs.storage.operation.duration (unit s)  →  cs_storage_operation_duration_seconds_{bucket,count,sum}
    cs.parity.comparisons (counter)         →  cs_parity_comparisons_total
    http.server.request.duration            →  http_server_request_duration_seconds_*   (labels: http_route, http_response_status_code)
"""
import json
import pathlib

DS = {"type": "prometheus", "uid": "prometheus"}
panels = []
y = 0


def row(title):
    global y
    panels.append({"type": "row", "title": title, "gridPos": {"h": 1, "w": 24, "x": 0, "y": y}, "collapsed": False})
    y += 1


def ts(title, exprs, unit="short", x=0, w=12, h=8, description="", stack=False):
    """A time-series panel. exprs: list of (PromQL, legend)."""
    panels.append({
        "type": "timeseries", "title": title, "description": description, "datasource": DS,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "fieldConfig": {"defaults": {"unit": unit, "custom": {"lineWidth": 1, "fillOpacity": 10 if stack else 0,
                                                              "stacking": {"mode": "normal" if stack else "none"}}},
                        "overrides": []},
        "options": {"legend": {"displayMode": "table", "placement": "bottom", "calcs": ["lastNotNull", "max"]},
                    "tooltip": {"mode": "multi"}},
        "targets": [{"datasource": DS, "expr": e, "legendFormat": l, "refId": chr(65 + i)} for i, (e, l) in enumerate(exprs)],
    })


def pair(left, right):
    """Two half-width panels side by side."""
    global y
    ts(**left, x=0, w=12)
    ts(**right, x=12, w=12)
    y += 8


def q(p, metric, by):
    return f"histogram_quantile({p}, sum by (le, {by}) (rate({metric}_bucket[$__rate_interval])))"


row("Migration phase: what mode are we in?")
pair(
    dict(title="Storage mode", unit="percent", description="Step lines mark every phase change; everything below lines up against them.",
         exprs=[("max(cs_storage_read_shadow_percent)", "shadow %"), ("max(cs_storage_read_split_percent)", "split %"),
                ("max(cs_storage_write_dual) * 100", "dual writes on (100 = yes)")]),
    dict(title="Mode version per pod", description="All pods must show the same version before a step counts as done (runbook).",
         exprs=[("cs_storage_mode_version", "{{instance}}")]),
)

row("Traffic and errors (RED: Rate, Errors, Duration)")
pair(
    dict(title="Requests/s by route and status", unit="reqps", stack=True,
         exprs=[("sum by (http_route, http_response_status_code) (rate(http_server_request_duration_seconds_count[$__rate_interval]))",
                 "{{http_route}} {{http_response_status_code}}")]),
    dict(title="Non-2xx responses/s", unit="reqps", description="The rollback drill's success criterion: this stays at zero.",
         exprs=[('sum by (http_route, http_response_status_code) (rate(http_server_request_duration_seconds_count{http_response_status_code!~"2.."}[$__rate_interval]))',
                 "{{http_route}} {{http_response_status_code}}")]),
)
pair(
    dict(title="HTTP p99 by route", unit="s",
         exprs=[(q(0.99, "http_server_request_duration_seconds", "http_route"), "p99 {{http_route}}")]),
    dict(title="HTTP p50 by route", unit="s",
         exprs=[(q(0.50, "http_server_request_duration_seconds", "http_route"), "p50 {{http_route}}")]),
)

row("Storage: per backend and role (spec §7.5)")
pair(
    dict(title="Storage p99 by backend / op / role", unit="s",
         exprs=[(q(0.99, "cs_storage_operation_duration_seconds", "backend, op, role"), "{{backend}} {{op}} {{role}}")]),
    dict(title="Storage p50 by backend / op / role", unit="s",
         exprs=[(q(0.50, "cs_storage_operation_duration_seconds", "backend, op, role"), "{{backend}} {{op}} {{role}}")]),
)
pair(
    dict(title="Storage calls/s by backend / op / role", unit="ops",
         exprs=[("sum by (backend, op, role) (rate(cs_storage_operation_duration_seconds_count[$__rate_interval]))", "{{backend}} {{op}} {{role}}")]),
    dict(title="Storage errors/s", unit="ops",
         exprs=[('sum by (backend, op, role) (rate(cs_storage_operation_duration_seconds_count{outcome="error"}[$__rate_interval]))', "{{backend}} {{op}} {{role}}")]),
)

row("Parity (ADR 0011): the gate for every ramp step")
pair(
    dict(title="Shadow comparisons/s by result", unit="ops", stack=True,
         description="mismatch = a CLOSED bucket differs: a real bug. race = open-bucket noise.",
         exprs=[("sum by (result) (rate(cs_parity_comparisons_total[$__rate_interval]))", "{{result}}")]),
    dict(title="Mismatch rate (mismatch ÷ (match + mismatch))", unit="percentunit",
         exprs=[('sum(rate(cs_parity_comparisons_total{result="mismatch"}[5m])) / '
                 'clamp_min(sum(rate(cs_parity_comparisons_total{result=~"match|mismatch"}[5m])), 1e-9)', "mismatch rate")]),
)
pair(
    # A counter that was never incremented doesn't exist yet in Prometheus, so "or vector(0)" draws an honest 0
    # instead of "No data", which is indistinguishable from "the metric pipeline is broken".
    dict(title="Things that must stay at zero", description="Secondary write failures, dropped shadow work, rejected mode changes.",
         exprs=[("sum(increase(cs_storage_secondary_write_failures_total[$__rate_interval])) or vector(0)", "secondary write failures"),
                ("sum(increase(cs_parity_shadow_dropped_total[$__rate_interval])) or vector(0)", "shadow dropped"),
                ("sum(increase(cs_storage_mode_reload_failures_total[$__rate_interval])) or vector(0)", "mode reload rejected")]),
    dict(title="Decisions/s by outcome", unit="ops", stack=True,
         description="If a migration silently changed answers, the mix would shift here.",
         exprs=[("sum by (outcome) (rate(cs_decisions_total[$__rate_interval]))", "{{outcome}}")]),
)

dashboard = {
    "uid": "countershift", "title": "CounterShift: migration", "tags": ["countershift"],
    "timezone": "utc", "refresh": "5s", "schemaVersion": 39, "version": 1,
    "time": {"from": "now-30m", "to": "now"}, "panels": panels,
}
out = pathlib.Path(__file__).parent / "grafana" / "dashboards" / "countershift.json"
out.write_text(json.dumps(dashboard, indent=2) + "\n", encoding="utf-8", newline="\n")
print(f"wrote {out} ({len(panels)} panels)")

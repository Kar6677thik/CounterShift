"""Draws the README charts, in light and dark variants, from the recorded results.

    python -I load/make_charts.py            # needs matplotlib; writes docs/images/

- lost-updates: numbers from docs/results/b1-lost-updates.md
- rehearsal:    computed from docs/results/data/m8-rehearsal-local.json (the raw rehearsal output)
- footprint:    numbers from docs/results/m8-footprint.md

Colours are the dataviz reference palette, validated for adjacent use in light and dark mode.
"""

import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Patch  # noqa: E402
from matplotlib.ticker import FixedLocator, NullLocator  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "images"

THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", muted="#898781", grid="#e1e0d9",
                  axis="#c3c2b7", band="#f0efec", series=["#2a78d6", "#eb6834"]),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", muted="#898781", grid="#2c2c2a",
                 axis="#383835", band="#2c2c2a", series=["#3987e5", "#d95926"]),
}


def style(ax, t, grid_axis="y"):
    ax.set_facecolor(t["surface"])
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    ax.spines["bottom"].set_color(t["axis"])
    ax.tick_params(colors=t["muted"], labelsize=9, length=0)
    ax.grid(axis=grid_axis, color=t["grid"], linewidth=0.8)
    ax.set_axisbelow(True)


def figure(t, *args, **kwargs):
    fig, axes = plt.subplots(*args, **kwargs)
    fig.patch.set_facecolor(t["surface"])
    return fig, axes


def save(fig, name, mode):
    OUT.mkdir(parents=True, exist_ok=True)
    fig.savefig(OUT / f"{name}-{mode}.svg", facecolor=fig.get_facecolor(), bbox_inches="tight", pad_inches=0.25)
    plt.close(fig)


def lost_updates_chart(mode):
    """Two small multiples sharing the strategy axis: correctness on the left, cost on the right."""
    t = THEMES[mode]
    rows = [  # (strategy, increments kept of 50,000, intended increments per second), from B1
        ("read-modify-write in a transaction", 1_050, 1_479),
        ("atomic upsert (ON CONFLICT … count + 1)", 50_000, 7_840),
        ("SELECT … FOR UPDATE", 50_000, 625),
        ("SERIALIZABLE + retry", 50_000, 142),
    ]
    fig, (left, right) = figure(t, 1, 2, figsize=(10, 2.9), sharey=True, gridspec_kw=dict(wspace=0.08, width_ratios=[1.15, 1]))
    for ax, column, xmax, title, fmt in (
        (left, 1, 62_000, "increments kept, of 50,000", "{:,}"),
        (right, 2, 9_800, "throughput (increments / s)", "{:,}"),
    ):
        style(ax, t, grid_axis="x")
        ax.grid(axis="y", visible=False)
        for i, row in enumerate(rows):
            y = len(rows) - 1 - i
            ax.barh(y, row[column], height=0.5, color=t["series"][0], linewidth=0, zorder=2)
            ax.text(row[column] + xmax * 0.015, y, fmt.format(row[column]), va="center", ha="left", fontsize=9.5, color=t["ink"])
        ax.set_xlim(0, xmax)
        ax.set_title(title, loc="left", fontsize=9.5, color=t["ink2"], pad=6)
        ax.xaxis.set_major_locator(NullLocator())
        ax.spines["bottom"].set_visible(False)
    left.axvline(50_000, color=t["ink2"], linewidth=1.1, linestyle=(0, (4, 3)), zorder=3)
    left.text(50_000, len(rows) - 0.4, "expected", ha="center", va="bottom", fontsize=8.5, color=t["ink2"])
    left.set_yticks(range(len(rows)))
    left.set_yticklabels([r[0] for r in reversed(rows)], fontsize=9.5, color=t["ink2"])
    fig.suptitle("50 concurrent writers × 1,000 increments on one bucket: the naive increment loses 97.9%",
                 x=0.0, ha="left", fontsize=11.5, color=t["ink"], y=1.04)
    save(fig, "lost-updates", mode)


def rehearsal_chart(mode):
    t = THEMES[mode]
    data = json.loads((ROOT / "docs/results/data/m8-rehearsal-local.json").read_text(encoding="utf-8"))
    steps = data["steps"]
    short = {
        "P0 baseline": "P0\npg only", "P1 dual writes": "P1\ndual", "P2 shadow 5%": "shadow\n5%",
        "P2 shadow 20%": "shadow\n20%", "P2 shadow 50%": "shadow\n50%", "P2 shadow 100%": "shadow\n100%",
        "P3 split 5%": "split\n5%", "P3 split 30%": "split\n30%", "P3 split 70%": "split\n70%",
        "ROLLBACK to postgres reads": "ROLL-\nBACK", "P3 split 70% (again)": "split\n70%", "P3 split 100%": "split\n100%",
        "P4 redis primary, pg dual": "P4\nredis 1st", "P5 redis only": "P5\nredis",
    }
    x = range(len(steps))
    fig, ax = figure(t, figsize=(10, 3.9))
    style(ax, t)
    # The split phase (Redis serving a share of reads) sits on a light band.
    ax.axvspan(5.5, 11.5, color=t["band"], linewidth=0, zorder=0)
    ax.text(5.62, 300, "Redis serves a share of reads", fontsize=8.5, color=t["muted"], va="center")
    ax.text(1.62, 300, "shadow ramp", fontsize=8.5, color=t["muted"], va="center")
    rollback = next(i for i, s in enumerate(steps) if s["name"].startswith("ROLLBACK"))
    ax.axvline(rollback, color=t["ink2"], linewidth=1, linestyle=(0, (2, 3)), zorder=1)
    series = [("/v1/events", "events (write path)"), ("/v1/decisions", "decisions (read path)")]
    for k, (route, label) in enumerate(series):
        ys = [s["http_p0.99"].get(route, 0) * 1000 for s in steps]
        ax.plot(x, ys, color=t["series"][k], linewidth=2, marker="o", markersize=5,
                markeredgecolor=t["surface"], markeredgewidth=1.5, zorder=3, label=label)
        ax.text(len(steps) - 0.7, ys[-1] * (1.35 if k == 0 else 0.72), label, color=t["ink2"], fontsize=9, va="center")
    ax.set_yscale("log")
    ax.yaxis.set_major_locator(FixedLocator([1, 3, 10, 30, 100, 300]))
    ax.yaxis.set_minor_locator(NullLocator())
    ax.set_yticks([1, 3, 10, 30, 100, 300])
    ax.set_yticklabels(["1 ms", "3", "10", "30", "100", "300"])
    ax.set_ylim(1, 400)
    ax.set_xticks(list(x))
    ax.set_xticklabels([short[s["name"]] for s in steps], fontsize=8.5, color=t["ink2"])
    ax.set_xlim(-0.5, len(steps) + 1.6)
    spike = next(i for i, s in enumerate(steps) if s["name"] == "P3 split 70%")
    ax.annotate("PostgreSQL write stall\n(one 30 s window)", xy=(spike, 195), xytext=(spike + 1.6, 120),
                fontsize=8.5, color=t["muted"], arrowprops=dict(arrowstyle="-", color=t["muted"], linewidth=0.8))
    ax.text(rollback + 0.12, 1.25, "rollback: 333 ms", fontsize=8.5, color=t["ink2"])
    ax.legend(loc="lower left", frameon=False, fontsize=9, labelcolor=t["ink2"], ncol=2, bbox_to_anchor=(0, 1.0))
    ax.set_ylabel("server-side p99, log scale", fontsize=9, color=t["muted"])
    ax.set_title("Migration under 600 req/s, step by step: 0 non-2xx responses and 0 parity mismatches in every step",
                 loc="left", fontsize=11.5, color=t["ink"], pad=26)
    save(fig, "rehearsal", mode)


def footprint_chart(mode):
    t = THEMES[mode]
    rows = [  # (label, MiB per 1M events, store index), from the footprint experiment
        ("PostgreSQL, as written (disk)", 552.1, 0),
        ("PostgreSQL, after VACUUM FULL (disk)", 456.7, 0),
        ("Redis, INFO used_memory growth (RAM)", 196.5, 1),
        ("Redis, sum of MEMORY USAGE (RAM)", 149.7, 1),
    ]
    fig, ax = figure(t, figsize=(8, 2.7))
    style(ax, t, grid_axis="x")
    ax.grid(axis="y", visible=False)
    for i, (label, value, store) in enumerate(rows):
        y = len(rows) - 1 - i
        ax.barh(y, value, height=0.5, color=t["series"][store], linewidth=0, zorder=2)
        ax.text(value + 8, y, f"{value:,.0f} MiB", va="center", ha="left", fontsize=9.5, color=t["ink"])
        ax.text(-10, y, label, va="center", ha="right", fontsize=9.5, color=t["ink2"])
    ax.legend(handles=[Patch(color=t["series"][0], label="PostgreSQL: row per bucket"),
                       Patch(color=t["series"][1], label="Redis: hash per counter")],
              loc="lower right", frameon=False, fontsize=9, labelcolor=t["ink2"])
    ax.set_xlim(0, 640)
    ax.set_yticks([])
    ax.xaxis.set_major_locator(NullLocator())
    ax.spines["bottom"].set_visible(False)
    ax.set_title("Storage per 1 million events (identical 3,356,445 buckets in both stores)",
                 loc="left", fontsize=11.5, color=t["ink"], pad=10)
    save(fig, "footprint", mode)


if __name__ == "__main__":
    plt.rcParams.update({"font.family": ["Segoe UI", "DejaVu Sans"], "svg.fonttype": "path"})
    for theme in THEMES:
        lost_updates_chart(theme)
        rehearsal_chart(theme)
        footprint_chart(theme)
    print("wrote", sorted(p.name for p in OUT.glob("*-*.svg")))

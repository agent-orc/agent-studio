#!/usr/bin/env python3
"""First-order wave model for the pipeline simulator Dossier (AGT-4028).

A small discrete-event model of one arrival wave through Ready -> Coding ->
Review -> Integration -> Done under four integration strategies. It exists to
check that the measured values of 17 to 23 November 2026 fit together and to
give first answers to the owner's example questions. It is not the simulator:
the two prototype cards and the calibration card replace every number here.

Every parameter is in PARAMS and is either a measured value (M) or an
assumption the owner sets (A). Standard library only, deterministic per seed.

Usage: python3 -I docs/pipeline-simulator/evidence/wave_model.py [--runs 200] [--set KEY=VALUE ...]
"""
import argparse
import heapq
import random
import statistics

PARAMS = {
    # Arrival (M): 57 cards in one minute on 17 November, three series of 19.
    "wave": 57, "series": 3,
    # Overlap (M): two series share a code file with 16 to 19 other cards, the
    # third with 0.7. Expressed as pair overlap probability within a series.
    "overlap_high": 0.95, "overlap_low": 0.04, "overlap_cross": 0.01,
    # Share of overlapping pairs that conflict textually at merge (A, calibrated
    # so that strict reproduces 21 of 57 cards needing a develop sync, the
    # observed count in the dispatcher replay of the same wave).
    "conflict_given_overlap": 0.18,
    # Coding (M): 10 slots, first run 25 to 140 min, typically 60.
    "coding_slots": 10, "first_run_median": 60, "first_run_min": 25, "first_run_max": 140,
    # Follow-up round duration (A): resumed session that includes develop.
    "round_min": 10, "round_max": 30,
    # Review (M): about 7 slots, 5 to 14 min with build only.
    "review_slots": 7, "review_min": 5, "review_max": 14,
    # Red rate (M via strict): 48 % of 8-card batches green -> per-card red.
    "batch_green": 0.48, "batch_size": 8,
    # Strict gate (M): about 40 min a batch; isolation per card (A) 20 to 25 min.
    "batch_minutes": 40, "isolation_min": 20, "isolation_max": 25,
    # Stale verdict threshold (A): wait after review pass before re-review.
    "stale_minutes": 240,
    # Fast as built (M): one card at a time, 15 to 55 min test run, repeat on red.
    "serial_test_min": 15, "serial_test_max": 55, "flaky": 0.10,
    # Fast as intended (M): compile check (A: 3 to 6 min), window of 6 merges or
    # 30 min, base suite about 10 min, red quarantined, only its area waits.
    "compile_min": 3, "compile_max": 6, "window_merges": 6, "window_minutes": 30,
    "base_suite": 10,
    # Tokens and cost (M): first run 26 to 67 million, 9 to 19 USD on Opus 5.5
    # medium; resumed Claude round 2.7 million, 1 to 2.4 USD; Codex round 26.6
    # million, 6 to 13 USD; review about 1 USD. Codex share of rounds (A).
    "first_tokens_min": 26e6, "first_tokens_max": 67e6, "first_usd_min": 9, "first_usd_max": 19,
    "claude_round_tokens": 2.7e6, "claude_round_usd_min": 1.0, "claude_round_usd_max": 2.4,
    "codex_round_tokens": 26.6e6, "codex_round_usd_min": 6, "codex_round_usd_max": 13,
    "codex_round_share": 0.0, "review_usd": 1.0,
    # Quota (M): about 18 USD list price equivalent per weekly percentage point.
    "usd_per_point": 18.0,
}

STRATEGIES = ("strict", "fast-as-built", "fast-as-intended", "dispatcher")


class Card:
    def __init__(self, cid, series, rng, p):
        self.id, self.series = cid, series
        self.defect = rng.random() < 1 - p["batch_green"] ** (1 / p["batch_size"])
        self.base = frozenset()
        self.rounds = 0
        self.syncs = 0
        self.reviews = 0
        self.first_usd = self.rework_usd = self.review_usd = 0.0
        self.first_tokens = self.rework_tokens = 0.0
        self.passed_at = None
        self.done_at = None
        self.gate_waits = []


class Model:
    def __init__(self, strategy, seed, p):
        self.s, self.p = strategy, p
        self.rng = random.Random(seed)
        n = p["wave"]
        self.cards = [Card(i, i % p["series"], self.rng, p) for i in range(n)]
        self.conflict = {}
        for a in range(n):
            for b in range(a + 1, n):
                sa, sb = self.cards[a].series, self.cards[b].series
                q = p["overlap_cross"] if sa != sb else (p["overlap_low"] if sa == p["series"] - 1 else p["overlap_high"])
                hit = self.rng.random() < q and self.rng.random() < p["conflict_given_overlap"]
                self.conflict[(a, b)] = self.conflict[(b, a)] = hit
        self.overlap_forecast = lambda a, b: self.cards[a].series == self.cards[b].series and self.cards[a].series != p["series"] - 1
        self.t = 0.0
        self.events = []
        self.seq = 0
        self.ready = list(range(n))
        self.coding_free, self.review_free = p["coding_slots"], p["review_slots"]
        self.coding_q, self.review_q, self.gate_q = [], [], []
        self.in_flight = set()
        self.merged = []
        self.gate_busy = False
        self.busy = {"coding": 0.0, "review": 0.0, "gate": 0.0}
        self.held_series = {}
        self.window_pending = 0
        self.window_open_at = None

    def at(self, dt, fn, *args):
        self.seq += 1
        heapq.heappush(self.events, (self.t + dt, self.seq, fn, args))

    def u(self, lo, hi):
        return self.rng.uniform(lo, hi)

    def first_run_minutes(self):
        p = self.p
        return min(p["first_run_max"], max(p["first_run_min"], self.rng.lognormvariate(0, 0.45) * p["first_run_median"]))

    # Coding
    def pick(self):
        while self.coding_free > 0:
            job = None
            if self.coding_q:
                job = self.coding_q.pop(0)
            else:
                for i, cid in enumerate(self.ready):
                    if self.s == "dispatcher" and any(self.overlap_forecast(cid, o) for o in self.in_flight):
                        continue
                    job = ("first", self.ready.pop(i))
                    break
            if job is None:
                return
            self.coding_free -= 1
            kind, cid = job
            c = self.cards[cid]
            self.in_flight.add(cid)
            c.base = frozenset(self.merged)
            p = self.p
            if kind == "first":
                dt = self.first_run_minutes()
                k = self.rng.random()
                c.first_tokens += p["first_tokens_min"] + k * (p["first_tokens_max"] - p["first_tokens_min"])
                c.first_usd += p["first_usd_min"] + k * (p["first_usd_max"] - p["first_usd_min"])
            else:
                dt = self.u(p["round_min"], p["round_max"])
                c.rounds += 1
                c.syncs += kind == "sync"
                if self.rng.random() < p["codex_round_share"]:
                    c.rework_tokens += p["codex_round_tokens"]
                    c.rework_usd += self.u(p["codex_round_usd_min"], p["codex_round_usd_max"])
                else:
                    c.rework_tokens += p["claude_round_tokens"]
                    c.rework_usd += self.u(p["claude_round_usd_min"], p["claude_round_usd_max"])
                if kind in ("fix", "fix-forward"):
                    c.defect = False
            self.busy["coding"] += dt
            self.at(dt, self.coded, cid)

    def coded(self, cid):
        self.coding_free += 1
        self.review_q.append(cid)
        self.review()
        self.pick()

    # Review
    def review(self):
        while self.review_free > 0 and self.review_q:
            cid = self.review_q.pop(0)
            self.review_free -= 1
            dt = self.u(self.p["review_min"], self.p["review_max"])
            self.busy["review"] += dt
            c = self.cards[cid]
            c.reviews += 1
            c.review_usd += self.p["review_usd"]
            self.at(dt, self.reviewed, cid)

    def reviewed(self, cid):
        self.review_free += 1
        self.cards[cid].passed_at = self.t
        self.gate_q.append(cid)
        self.review()
        self.gate()

    def back(self, cid, kind):
        self.coding_q.append((kind, cid))
        self.pick()

    def conflicts(self, cid):
        c = self.cards[cid]
        return any(self.conflict[(cid, m)] for m in self.merged if m not in c.base)

    def publish(self, cid):
        c = self.cards[cid]
        c.gate_waits.append(self.t - c.passed_at)
        self.merged.append(cid)
        self.in_flight.discard(cid)
        c.done_at = self.t
        if self.held_series.get(c.series) == cid:
            del self.held_series[c.series]

    # Integration
    def gate(self):
        if self.gate_busy or not self.gate_q:
            return
        p = self.p
        if self.s == "strict":
            members, now = [], self.t
            while self.gate_q and len(members) < p["batch_size"]:
                cid = self.gate_q.pop(0)
                if now - self.cards[cid].passed_at > p["stale_minutes"]:
                    self.review_q.append(cid)
                elif self.conflicts(cid):
                    self.back(cid, "sync")
                else:
                    members.append(cid)
            self.review()
            if not members:
                return self.gate()
            self.gate_busy = True
            self.busy["gate"] += p["batch_minutes"]
            self.at(p["batch_minutes"], self.batch_done, members)
        else:
            if self.s in ("fast-as-intended", "dispatcher"):
                pick = next((i for i, cid in enumerate(self.gate_q) if self.held_series.get(self.cards[cid].series, cid) == cid), None)
                if pick is None:
                    return
                cid = self.gate_q.pop(pick)
            else:
                cid = self.gate_q.pop(0)
            if self.conflicts(cid):
                self.back(cid, "sync")
                return self.gate()
            self.gate_busy = True
            if self.s == "fast-as-built":
                dt = self.u(p["serial_test_min"], p["serial_test_max"])
                red = self.cards[cid].defect or self.rng.random() < p["flaky"]
                if red:
                    dt += self.u(p["serial_test_min"], p["serial_test_max"])
            else:
                dt = self.u(p["compile_min"], p["compile_max"])
            self.busy["gate"] += dt
            self.at(dt, self.single_done, cid)

    def batch_done(self, members):
        if not any(self.cards[m].defect for m in members):
            for m in members:
                self.publish(m)
            self.gate_busy = False
            self.pick()
            return self.gate()
        self.isolate(members)

    def isolate(self, members):
        if not members:
            self.gate_busy = False
            self.pick()
            return self.gate()
        cid, rest = members[0], members[1:]
        dt = self.u(self.p["isolation_min"], self.p["isolation_max"])
        self.busy["gate"] += dt
        self.at(dt, self.isolated, cid, rest)

    def isolated(self, cid, rest):
        if self.cards[cid].defect:
            self.back(cid, "fix")
        elif self.conflicts(cid):
            self.back(cid, "sync")
        else:
            self.publish(cid)
        self.isolate(rest)

    def single_done(self, cid):
        self.gate_busy = False
        c = self.cards[cid]
        if self.s == "fast-as-built" and c.defect:
            self.back(cid, "fix")
        else:
            defect = c.defect
            self.publish(cid)
            if defect:
                # Fast as intended: the window finds the red, the test is
                # quarantined, and only the card's area waits for a repair.
                self.window_pending += 1
                self.at(self.p["window_minutes"] / 2 + self.p["base_suite"], self.window_red, cid)
        self.pick()
        self.gate()

    def window_red(self, cid):
        c = self.cards[cid]
        self.held_series.setdefault(c.series, cid)
        c.defect = True
        c.done_at = None
        self.coding_q.insert(0, ("fix-forward", cid))
        self.pick()

    def run(self):
        self.pick()
        while self.events:
            self.t, _, fn, args = heapq.heappop(self.events)
            fn(*args)
        return self


def pct(values, q):
    values = sorted(values)
    if not values:
        return float("nan")
    return values[min(len(values) - 1, max(0, int(round(q * (len(values) - 1)))))]


def summarize(strategy, runs, p):
    rows = []
    for seed in range(runs):
        m = Model(strategy, seed, p).run()
        done = [c for c in m.cards if c.done_at is not None]
        span = max(c.done_at for c in done)
        waits = [w for c in m.cards for w in c.gate_waits]
        first = sum(c.first_usd for c in m.cards)
        rework = sum(c.rework_usd for c in m.cards) + sum(max(0, c.reviews - 1) * p["review_usd"] for c in m.cards)
        reviews_first = sum(min(1, c.reviews) * p["review_usd"] for c in m.cards)
        rows.append({
            "done": len(done), "hours": span / 60, "per_hour": len(done) / (span / 60),
            "lead_p50": pct([c.done_at for c in done], 0.5), "lead_p95": pct([c.done_at for c in done], 0.95),
            "wait_p50": pct(waits, 0.5), "wait_p95": pct(waits, 0.95),
            "synced": sum(1 for c in m.cards if c.syncs > 0), "max_rounds": max(c.rounds for c in m.cards),
            "usd_first": first + reviews_first, "usd_rework": rework,
            "tokens_first": sum(c.first_tokens for c in m.cards), "tokens_rework": sum(c.rework_tokens for c in m.cards),
            "util": {k: v / (span * (p["coding_slots"] if k == "coding" else p["review_slots"] if k == "review" else 1)) for k, v in m.busy.items()},
        })
    mean = lambda k: statistics.fmean(r[k] for r in rows)
    util = {k: statistics.fmean(r["util"][k] for r in rows) for k in ("coding", "review", "gate")}
    total = mean("usd_first") + mean("usd_rework")
    return {
        "strategy": strategy, "done": mean("done"), "hours": mean("hours"), "per_hour": mean("per_hour"),
        "lead_p50": mean("lead_p50"), "lead_p95": mean("lead_p95"), "wait_p50": mean("wait_p50"), "wait_p95": mean("wait_p95"),
        "synced": mean("synced"), "max_rounds": max(r["max_rounds"] for r in rows),
        "usd_first": mean("usd_first"), "usd_rework": mean("usd_rework"), "usd_total": total,
        "rework_share": mean("usd_rework") / total, "points": total / p["usd_per_point"],
        "tokens_first": mean("tokens_first"), "tokens_rework": mean("tokens_rework"),
        "util": util, "bottleneck": max(util, key=util.get),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", type=int, default=200)
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                    help="override a parameter, for example --set review_slots=9")
    args = ap.parse_args()
    p = dict(PARAMS)
    for item in args.set:
        key, value = item.split("=", 1)
        if key not in p:
            raise SystemExit(f"unknown parameter {key}")
        p[key] = type(p[key])(float(value)) if isinstance(p[key], int) else float(value)
    changed = ", ".join(args.set) or "none"
    print(f"Wave model, {p['wave']} cards at t=0, {args.runs} seeded runs per strategy, overrides: {changed}")
    print("strategy           cards/h  drain h  lead p50/p95 min  gate wait p50/p95 min  synced  max rounds  "
          "USD first  USD rework  rework %  points  Mtok first  Mtok rework  util coding/review/gate  bottleneck")
    for s in STRATEGIES:
        r = summarize(s, args.runs, p)
        u = r["util"]
        print(f"{s:<18} {r['per_hour']:7.2f}  {r['hours']:7.1f}  {r['lead_p50']:6.0f} / {r['lead_p95']:<6.0f}    "
              f"{r['wait_p50']:6.0f} / {r['wait_p95']:<6.0f}         {r['synced']:5.1f}  {r['max_rounds']:10d}  "
              f"{r['usd_first']:9.0f}  {r['usd_rework']:10.0f}  {100 * r['rework_share']:7.1f}  {r['points']:6.1f}  "
              f"{r['tokens_first'] / 1e6:10.0f}  {r['tokens_rework'] / 1e6:11.0f}  "
              f"{u['coding']:.2f} / {u['review']:.2f} / {u['gate']:.2f}       {r['bottleneck']}")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Replay of dispatcher strategies on one recorded wave (Dossier docs/dispatcher-personas).

Every starter persona plans the same wave with the information it would have had
before the start (forecast files, modules or keyword areas). The replay then counts,
with the files each card really changed, which overlapping cards it would have run
at the same time.

Model, stated so the numbers can be checked:
- N equal slots. A card occupies one slot for its duration and integrates at the end.
- Durations: "unit" (every card 1) or "size" (1 + real code files / 15).
- Integration lag (default 0): time between the end of a card's coding run and its
  integration (review aspects, batch gate). During the lag the card holds no slot but
  is still exposed. Check "running" compares a candidate only with running cards, as
  today's pick-gate does (ActiveRuns); "inflight" also compares it with cards whose run
  ended but that have not integrated yet.
- A package of cards runs as one unit whose duration is the sum of its members.
- A card is exposed when another card that shares a real code file integrated while
  it was running. The later of the two needs a develop sync. A shared file is an upper
  bound for a conflict: Git merges many shared-file changes cleanly.
- Hub files (registration files almost every card touches) are ignored by the
  file- and module-based strategies, because their edits are append-only and usually
  merge cleanly. They still count as real overlap in the measurement.

Today's pick-gate (ProjectRunner.PredictParallelism) already declares a scope: the parent
directory of every backend/, frontend/, src/, docs/ or tools/ path named in the card's
title or prompt.md, compared by prefix (ParallelSlotPolicy.FirstScopeConflict). The real
prompts of the wave are not in git, so "Queue keeper, prompt paths" applies that rule to
the cited paths of each finding. It is an upper bound for how often the rule could fire
and is reported outside the scored comparison set.

Usage: python3 -I replay.py [wave.json] [slots]
Standard library only.
"""
import itertools
import json
import os
import re
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
WAVE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "wave-2026-11-17.json")
SLOTS = int(sys.argv[2]) if len(sys.argv) > 2 else 4

wave = json.load(open(WAVE, encoding="utf-8"))
HUB = set(wave["hubFiles"])
cards = {c["key"]: c for c in wave["cards"]}
keys = sorted(cards)
real = {k: set(cards[k]["codeFiles"]) for k in keys}
fc = {k: set(cards[k]["forecastFiles"]) for k in keys}
kw = {k: set(cards[k]["keywordAreas"]) or {"general"} for k in keys}


def module(path):
    p = path.split("/")
    if path.startswith("frontend/src/app/features/") and len(p) > 4:
        return "/".join(p[:5])
    if path.startswith("frontend/src/app/") and len(p) > 3:
        return "/".join(p[:4])
    if path.startswith("frontend/e2e/") and len(p) > 2:
        return "/".join(p[:3])
    if path.startswith("backend/") and len(p) > 2:
        return "/".join(p[:3])
    return "/".join(p[:2])


mods = {k: {module(f) for f in fc[k] - HUB} or {"unknown:" + k} for k in keys}

# Lane-entry order: newest arrival on top. All 57 cards arrived in one minute, so the
# newest key comes first.
ORDER = sorted(keys, reverse=True)


def union(sets):
    out = set()
    for s in sets:
        out |= s
    return out


# --- what each strategy believes about overlap -------------------------------------
def overlap_keyword(a, b):
    return bool(union(kw[k] for k in a) & union(kw[k] for k in b))


def overlap_module(a, b):
    return bool(union(mods[k] for k in a) & union(mods[k] for k in b))


def overlap_files(a, b):
    return bool(union(fc[k] for k in a) - HUB & union(fc[k] for k in b) - HUB)


def overlap_real(a, b):
    return bool(union(real[k] for k in a) & union(real[k] for k in b))


# Today's rule: ProjectRunner.ScopePathRegex, parent directory, prefix overlap.
SCOPE_PATH = re.compile(r"\b((?:backend|frontend|src|docs|tools)/[A-Za-z0-9_./-]+)", re.IGNORECASE)
prompt_scope = {k: {p.rsplit("/", 1)[0] for p in cards[k]["citedFiles"] if SCOPE_PATH.match(p)} for k in keys}


def _prefix_overlap(x, y):
    return x == y or x.startswith(y + "/") or y.startswith(x + "/")


def overlap_prompt_paths(a, b):
    sa, sb = union(prompt_scope[k] for k in a), union(prompt_scope[k] for k in b)
    if not sa or not sb:  # unknown scope is admitted optimistically
        return False
    return any(_prefix_overlap(x, y) for x in sa for y in sb)


def pair_quality(pred):
    pairs = list(itertools.combinations(keys, 2))
    truth = {p for p in pairs if real[p[0]] & real[p[1]]}
    guess = {p for p in pairs if pred([p[0]], [p[1]])}
    tp = len(truth & guess)
    precision = tp / len(guess) if guess else 0.0
    recall = tp / len(truth) if truth else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return dict(realPairs=len(truth), predictedPairs=len(guess), precision=round(precision, 2),
                recall=round(recall, 2), f1=round(f1, 2))


# --- packer: packages and chains before the start ----------------------------------
# Size prior per source kind: median real code files of earlier delivered cards of the
# same source. Text signals do not predict size on this wave: the correlation of real
# code files with forecast size is 0.10 over the 55 cards with source text (printed
# below). The counts of acceptance criteria (0.08) and words (0.12) were measured on the
# source finding text, which this wave file does not keep. The replay uses the wave's
# own group medians as a stand-in for that learned prior.
def _median(xs):
    xs = sorted(xs)
    return xs[len(xs) // 2] if xs else 0


def _pearson(xs, ys):
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    return sxy / (sxx * syy) ** 0.5 if sxx and syy else 0.0


SIZE_PRIOR = {g: _median([len(real[k]) for k in keys if cards[k]["group"] == g])
              for g in {cards[k]["group"] for k in keys}}
PACKAGE_MAX_PRIOR_FILES = 25
PACKAGE_MAX_MEMBERS = 3


def packer_units():
    strong = defaultdict(set)
    for a, b in itertools.combinations(keys, 2):
        if cards[a]["group"] == cards[b]["group"] and len((fc[a] - HUB) & (fc[b] - HUB)) >= 2:
            strong[a].add(b)
            strong[b].add(a)
    used, units = set(), []
    for k in ORDER:
        if k in used:
            continue
        pkg = [k]
        used.add(k)
        for x in sorted(strong[k] - used, key=lambda x: (-len((fc[x] - HUB) & (fc[k] - HUB)), x)):
            prior = sum(SIZE_PRIOR[cards[i]["group"]] for i in pkg + [x])
            if len(pkg) < PACKAGE_MAX_MEMBERS and prior <= PACKAGE_MAX_PRIOR_FILES:
                pkg.append(x)
                used.add(x)
        units.append(pkg)
    after = {}
    for i, u in enumerate(units):
        for j in range(i):
            if overlap_files(u, units[j]):
                after[i] = [j]
                break
    return units, after


# --- one simulation ---------------------------------------------------------------
def simulate(admit, units=None, after=None, learn=False, durations="unit", lag=0.0, check="running"):
    units = units or [[k] for k in ORDER]
    after = after or {}
    dur = (lambda u: float(len(u))) if durations == "unit" else (
        lambda u: sum(1 + len(real[k]) / 15 for k in u))
    queue = list(range(len(units)))
    running, pending, done, start, integrated = [], [], {}, {}, []
    t, busy = 0.0, 0.0
    locks, pairs_seen, exposed = set(), set(), set()

    def ready(q):
        return all(p in done for p in after.get(q, []))

    def allowed(q):
        others = [units[r] for r, _ in running]
        if check == "inflight":
            others += [units[r] for r, _ in pending]
        if not all(admit(units[q], o) for o in others):
            return False
        if learn:
            mine = union(mods[k] for k in units[q])
            for o in others:
                if mine & union(mods[k] for k in o) & locks:
                    return False
                if any((min(a, b), max(a, b)) in pairs_seen for a in units[q] for b in o):
                    return False
        return True

    while queue or running or pending:
        moved = True
        while len(running) < SLOTS and moved:
            moved = False
            for q in list(queue):
                if ready(q) and allowed(q):
                    queue.remove(q)
                    running.append((q, t + dur(units[q])))
                    start[q] = t
                    moved = True
                    break
            if not moved and not running and not pending and queue:
                q = next(x for x in queue if ready(x))
                queue.remove(q)
                running.append((q, t + dur(units[q])))
                start[q] = t
                moved = True
        nxt = min([end for _, end in running] + [end for _, end in pending])
        busy += sum(min(end, nxt) - t for _, end in running)
        t = nxt
        ended = [r for r in running if r[1] == t]
        running = [r for r in running if r[1] != t]
        pending += [(q, t + lag) for q, _ in ended]
        finished = sorted(r for r in pending if r[1] == t)
        pending = [r for r in pending if r[1] != t]
        for q, _ in finished:
            for p, tp in integrated:
                if tp > start[q]:
                    for a in units[q]:
                        for b in units[p]:
                            shared = real[a] & real[b]
                            if shared:
                                exposed.add((min(a, b), max(a, b)))
                                if learn:
                                    locks.update(module(f) for f in shared - HUB)
                                    pairs_seen.add((min(a, b), max(a, b)))
            integrated.append((q, t))
            done[q] = t
    finish = {k: done[i] for i, u in enumerate(units) for k in u}
    needs_sync = {a if finish[a] >= finish[b] else b for a, b in exposed}
    absorbed = sum(1 for u in units for a, b in itertools.combinations(u, 2) if real[a] & real[b])
    return dict(runs=len(units), makespan=round(t, 1), slotUse=round(busy / (SLOTS * t), 2),
                exposedPairs=len(exposed), cardsNeedingSync=len(needs_sync),
                overlapsInsidePackages=absorbed)


def admit_all(a, b):
    return True


def run(durations, lag=0.0, check="running"):
    pk_units, pk_after = packer_units()
    opt = dict(durations=durations, lag=lag, check=check)
    rows = [
        ("Queue keeper (baseline)", simulate(admit_all, **opt), None),
        ("Area warden, keyword areas", simulate(lambda a, b: not overlap_keyword(a, b), **opt), overlap_keyword),
        ("Area warden, forecast modules", simulate(lambda a, b: not overlap_module(a, b), **opt), overlap_module),
        ("Surveyor", simulate(lambda a, b: not overlap_files(a, b), **opt), overlap_files),
        ("Historian", simulate(lambda a, b: not overlap_files(a, b), learn=True, **opt), overlap_files),
        ("Packer", simulate(lambda a, b: not overlap_files(a, b), units=pk_units, after=pk_after, **opt), overlap_files),
    ]
    bound = simulate(lambda a, b: not overlap_real(a, b), **opt)
    return rows, bound, pk_units


def prompt_path_row(durations, bound, lag=0.0, check="running"):
    """Today's prompt-path rule, scored against the same bound but outside the min-max set."""
    r = simulate(lambda a, b: not overlap_prompt_paths(a, b), durations=durations, lag=lag, check=check)
    return dict(name="Queue keeper, prompt paths (upper bound, unscored)", **r,
                roundsPerCard=round((r["runs"] + r["cardsNeedingSync"]) / len(keys), 2),
                parallelism=round(min(1.0, r["slotUse"] / bound["slotUse"]), 2) if bound["slotUse"] else 0.0)


WEIGHTS = {"conflicts": 30, "tokens": 25, "rework": 10, "forecast": 10, "parallelism": 10, "lead": 5}
GATE = 0.8  # value is multiplied by min(1, parallelism / GATE)


def score(rows, bound):
    n = len(keys)
    table = []
    for name, r, pred in rows:
        rounds = (r["runs"] + r["cardsNeedingSync"]) / n
        table.append(dict(name=name, **r,
                          conflictsPer100=round(100 * r["cardsNeedingSync"] / n, 1),
                          roundsPerCard=round(rounds, 2),
                          reworkShare=round(r["cardsNeedingSync"] / (r["runs"] + r["cardsNeedingSync"]), 2),
                          forecastF1=pair_quality(pred)["f1"] if pred else 0.0,
                          parallelism=round(min(1.0, r["slotUse"] / bound["slotUse"]), 2)))

    def minmax(field, x):
        vals = [row[field] for row in table]
        lo, hi = min(vals), max(vals)
        return 100.0 if hi == lo else 100.0 * (hi - x) / (hi - lo)

    for row in table:
        s = {"conflicts": minmax("conflictsPer100", row["conflictsPer100"]),
             "tokens": minmax("roundsPerCard", row["roundsPerCard"]),
             "rework": minmax("reworkShare", row["reworkShare"]),
             "forecast": 100 * row["forecastF1"],
             "parallelism": 100 * row["parallelism"],
             "lead": minmax("makespan", row["makespan"])}
        plain = sum(WEIGHTS[c] * s[c] for c in WEIGHTS) / sum(WEIGHTS.values())
        row["scores"] = {c: round(v) for c, v in s.items()}
        row["valuePlain"] = round(plain)
        row["value"] = round(plain * min(1.0, row["parallelism"] / GATE))
    return table


if __name__ == "__main__":
    print(f"wave {wave['id']}: {len(keys)} cards, {SLOTS} slots")
    print("pair detection", json.dumps({
        "keywordAreas": pair_quality(overlap_keyword),
        "forecastModules": pair_quality(overlap_module),
        "forecastFiles": pair_quality(overlap_files),
        "citedFilesOnly": pair_quality(lambda a, b: bool(
            union(set(cards[k]["citedFiles"]) for k in a) & union(set(cards[k]["citedFiles"]) for k in b))),
        "promptPathScope": pair_quality(overlap_prompt_paths),
    }, indent=1))
    for durations in ("unit", "size"):
        rows, bound, pk = run(durations)
        print(f"\n== durations: {durations}; conflict-free bound: {json.dumps(bound)}")
        for row in score(rows, bound):
            print(json.dumps(row))
    print("\nsize prior (median real code files per source):", SIZE_PRIOR)
    texted = [k for k in keys if cards[k]["hasSourceText"]]
    print("size signal: correlation of real code files with forecast files, cited files"
          f" over {len(texted)} cards with source text:",
          round(_pearson([len(cards[k]["forecastFiles"]) for k in texted], [len(real[k]) for k in texted]), 2),
          round(_pearson([len(cards[k]["citedFiles"]) for k in texted], [len(real[k]) for k in texted]), 2))
    print("\npackages:", [[cards[k]["label"] for k in u] for u in pk if len(u) > 1])
    print("\n== today's prompt-path rule, unit and size durations, instant integration")
    for durations in ("unit", "size"):
        _, bound, _ = run(durations)
        print(durations, json.dumps(prompt_path_row(durations, bound)))
    print("\n== exposure window: integration lag in card durations, unit durations")
    for lag in (1, 2):
        for check in ("running", "inflight"):
            rows, bound, _ = run("unit", lag=lag, check=check)
            table = score(rows, bound)
            pp = prompt_path_row("unit", bound, lag=lag, check=check)
            print(f"lag {lag}, check {check}: bound slotUse {bound['slotUse']} makespan {bound['makespan']} syncs {bound['cardsNeedingSync']}")
            for row in table + [pp]:
                print("  " + json.dumps({f: row.get(f) for f in ("name", "cardsNeedingSync", "exposedPairs", "slotUse", "parallelism", "makespan", "roundsPerCard", "valuePlain", "value")}))

#!/usr/bin/env python3
"""Replay of dispatcher strategies on one recorded wave (Dossier docs/dispatcher-personas).

Every starter persona plans the same wave with the information it would have had
before the start (forecast files, modules or keyword areas). The replay then counts,
with the files each card really changed, which overlapping cards it would have run
at the same time.

Model, stated so the numbers can be checked:
- N equal slots. A card occupies one slot for its duration and integrates at the end.
- Durations: "unit" (every card 1) or "size" (1 + real code files / 15).
- A package of cards runs as one unit whose duration is the sum of its members.
- A card is exposed when another card that shares a real code file integrated while
  it was running. The later of the two needs a develop sync. A shared file is an upper
  bound for a conflict: Git merges many shared-file changes cleanly.
- Hub files (registration files almost every card touches) are ignored by the
  file- and module-based strategies, because their edits are append-only and usually
  merge cleanly. They still count as real overlap in the measurement.

Usage: python3 -I replay.py [wave.json] [slots]
Standard library only.
"""
import itertools
import json
import os
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
# same source. Text signals do not predict size on this wave (correlation of real code
# files with acceptance criteria 0.08, with words 0.12, with forecast size 0.10). The
# replay uses the wave's own group medians as a stand-in for that learned prior.
def _median(xs):
    xs = sorted(xs)
    return xs[len(xs) // 2] if xs else 0


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
def simulate(admit, units=None, after=None, learn=False, durations="unit"):
    units = units or [[k] for k in ORDER]
    after = after or {}
    dur = (lambda u: float(len(u))) if durations == "unit" else (
        lambda u: sum(1 + len(real[k]) / 15 for k in u))
    queue = list(range(len(units)))
    running, done, start, integrated = [], {}, {}, []
    t, busy = 0.0, 0.0
    locks, pairs_seen, exposed = set(), set(), set()

    def ready(q):
        return all(p in done for p in after.get(q, []))

    def allowed(q):
        others = [units[r] for r, _ in running]
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

    while queue or running:
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
            if not moved and not running and queue:
                q = next(x for x in queue if ready(x))
                queue.remove(q)
                running.append((q, t + dur(units[q])))
                start[q] = t
                moved = True
        nxt = min(end for _, end in running)
        busy += sum(min(end, nxt) - t for _, end in running)
        t = nxt
        finished = sorted(r for r in running if r[1] == t)
        running = [r for r in running if r[1] != t]
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


def run(durations):
    pk_units, pk_after = packer_units()
    rows = [
        ("Queue keeper (baseline)", simulate(admit_all, durations=durations), None),
        ("Area warden, keyword areas", simulate(lambda a, b: not overlap_keyword(a, b), durations=durations), overlap_keyword),
        ("Area warden, forecast modules", simulate(lambda a, b: not overlap_module(a, b), durations=durations), overlap_module),
        ("Surveyor", simulate(lambda a, b: not overlap_files(a, b), durations=durations), overlap_files),
        ("Historian", simulate(lambda a, b: not overlap_files(a, b), learn=True, durations=durations), overlap_files),
        ("Packer", simulate(lambda a, b: not overlap_files(a, b), units=pk_units, after=pk_after, durations=durations), overlap_files),
    ]
    bound = simulate(lambda a, b: not overlap_real(a, b), durations=durations)
    return rows, bound, pk_units


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
    }, indent=1))
    for durations in ("unit", "size"):
        rows, bound, pk = run(durations)
        print(f"\n== durations: {durations}; conflict-free bound: {json.dumps(bound)}")
        for row in score(rows, bound):
            print(json.dumps(row))
    print("\nsize prior (median real code files per source):", SIZE_PRIOR)
    print("\npackages:", [[cards[k]["label"] for k in u] for u in pk if len(u) > 1])

#!/usr/bin/env python3
"""Checks for the Dossier docs/dispatcher-personas (AGT-3974). Standard library only.

1. Mirrors ConceptWorkbenchContract: required descriptor keys, bounded-slice acceptance
   scopes, open-ended prompt phrases, dependsOn ids (known, earlier, not self, unique),
   and the four concept sections plus house-style CSS in index.html; every page listed
   under workbench "pages" is a self-contained document whose generated regions are
   current (build.py --check).
2. Applies today's pick-gate scope rule (ProjectRunner.ScopePathRegex, parent directory,
   prefix overlap) to the promoted prompts of the DS slices and of PT-13 to PT-20 of
   docs/persona-team, and prints which pairs that rule keeps apart. A promoted prompt is
   "Implement the approved concept described in `<dossier path>` ..." plus the item's
   promptMarkdown (ConceptPromotionService.BuildPrompt).
3. Checks that slices whose Area lines share a directory are ordered by dependsOn
   (directly or transitively).
4. Runs replay.py (4 slots, unit durations) and compares every cell of the replay
   results table in replay.html with the script output, so the published figures
   cannot drift from the evidence.
5. Checks that the wave card parts join to cardCount distinct cards in key order and
   that each part names its first and last card correctly.
6. Runs review_input.py: every Dossier file reaches the concept review whole.

Usage: python3 -I check_dossier.py [repository root]
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.abspath(os.path.join(HERE, "..", "..", ".."))
DOSSIER = os.path.join(ROOT, "docs", "dispatcher-personas")
failures = []

w = json.load(open(os.path.join(DOSSIER, "workbench.json"), encoding="utf-8"))
for key in ("schemaVersion", "id", "title", "summary", "entrypoint", "status", "phase",
            "updatedAt", "sourceTaskKeys", "implementationTasks"):
    if key not in w:
        failures.append(f"workbench.json {key} is required")
if "AGT-3974" not in w.get("sourceTaskKeys", []):
    failures.append("sourceTaskKeys lacks AGT-3974")
if w.get("status") != "decision-pending" or w.get("entrypoint") != "index.html":
    failures.append("status or entrypoint unexpected")

OPEN = re.compile(r"\b(?:implement|deliver|complete)\s+(?:all|every)\b.{0,80}\brecommendations?\b", re.I | re.S)
items = w["implementationTasks"]
index = {}
for i, t in enumerate(items):
    if t["id"] in index:
        failures.append(f"duplicate id {t['id']}")
    index[t["id"]] = i
    sc = t.get("acceptanceScope") or {}
    if sc.get("deliveryMode") != "bounded-slice" or not sc.get("slice") or not sc.get("criteria"):
        failures.append(f"{t['id']} acceptanceScope incomplete")
    if not t.get("title") or not t.get("promptMarkdown"):
        failures.append(f"{t['id']} title or promptMarkdown missing")
    if OPEN.search(t["title"] + "\n" + t["promptMarkdown"]):
        failures.append(f"{t['id']} is open-ended")
for i, t in enumerate(items):
    for d in t.get("dependsOn", []):
        j = index.get(d)
        if j is None or j >= i:
            failures.append(f"{t['id']} dependsOn {d} is unknown, self or later")

html = open(os.path.join(DOSSIER, "index.html"), encoding="utf-8").read()
if "<!doctype html>" not in html.lower() or "<style" not in html.lower():
    failures.append("index.html not self-contained")
for section in ("alternatives", "recommendation", "evidence", "open-decisions"):
    if f'data-concept-section="{section}"' not in html:
        failures.append(f"index.html lacks {section}")
for d in w.get("openDecisions", []):
    if f'data-decision-id="{d["id"]}"' not in html:
        failures.append(f"decision {d['id']} not in index.html")
pages = w.get("pages", [])
if not pages or pages[0] != "index.html":
    failures.append("workbench.json pages must start with index.html")
for page in pages:
    path = os.path.join(DOSSIER, page)
    text = open(path, encoding="utf-8").read() if os.path.isfile(path) else ""
    if "<!doctype html>" not in text.lower() or "<style" not in text.lower() or "<!--g:NAV-->" not in text:
        failures.append(f"{page} missing or not a self-contained Dossier page")
anchors = {page: set(re.findall(r' id="([^"]+)"', open(os.path.join(DOSSIER, page), encoding="utf-8").read()))
           for page in pages if os.path.isfile(os.path.join(DOSSIER, page))}
for page in anchors:
    text = open(os.path.join(DOSSIER, page), encoding="utf-8").read()
    for href in re.findall(r'href="([^"]+)"', text):
        target, _, anchor = href.replace("&amp;", "&").partition("#")
        if ":" in href:
            continue
        target = target or page
        if not os.path.isfile(os.path.join(DOSSIER, target)):
            failures.append(f"{page}: link {href} has no file")
        elif anchor and anchor not in anchors.get(target, set()):
            failures.append(f"{page}: link {href} has no anchor")
build = subprocess.run([sys.executable, "-I", os.path.join(DOSSIER, "build.py"), "--check"],
                       capture_output=True, text=True)
if not build.stdout.startswith("build: pages up to date"):
    failures.append("generated page regions are stale; run build.py")
print("contract:", "ok" if not failures else failures)

# --- 2. today's scope rule on promoted prompts -------------------------------------
RX = re.compile(r"\b((?:backend|frontend|src|docs|tools)/[A-Za-z0-9_./-]+)", re.I)


def scope(path, item):
    text = item["title"] + "\n" + f"Implement the approved concept described in `{path}`.\n" + item["promptMarkdown"]
    out = []
    for m in RX.findall(text):
        i = m.rfind("/")
        out.append(m[:i] if i > 0 else m)
    return list(dict.fromkeys(out))[:24]


def norm(p):
    return p.strip().replace("\\", "/").strip("/")


def overlap(a, b):
    for x in a:
        for y in b:
            nx, ny = norm(x), norm(y)
            if nx and ny and (nx == ny or nx.startswith(ny + "/") or ny.startswith(nx + "/")):
                return nx if len(nx) <= len(ny) else ny
    return None


ds = {t["id"]: scope("docs/dispatcher-personas/index.html", t) for t in items}
pt_items = json.load(open(os.path.join(ROOT, "docs", "persona-team", "workbench.json"), encoding="utf-8"))["implementationTasks"]
pt = {t["id"]: scope("docs/persona-team/index.html", t) for t in pt_items
      if t["id"] in {f"PT-{n}" for n in range(13, 21)}}
print("\nDS slice against PT-13 to PT-20, shared prefix under today's rule:")
for a in ds:
    row = {b: overlap(ds[a], pt[b]) for b in pt}
    kept = [b for b, v in row.items() if v]
    print(f"  {a}: kept apart from {len(kept)} of {len(pt)} ({', '.join(sorted(set(v for v in row.values() if v)))})")
    if a in ("DS-5", "DS-8") and len(kept) != len(pt):
        failures.append(f"{a} not separated from every PT addendum card")
print("  DS-5 and PT-13 share:", overlap([p for p in ds["DS-5"] if not p.startswith("docs/")],
                                         [p for p in pt["PT-13"] if not p.startswith("docs/")]))

# --- 3. same-area slices are ordered ------------------------------------------------
deps = {t["id"]: set(t.get("dependsOn", [])) for t in items}


def ancestors(x, seen=None):
    seen = set() if seen is None else seen
    for d in deps[x]:
        if d not in seen:
            seen.add(d)
            ancestors(d, seen)
    return seen


def area(item):
    line = next((l for l in item["promptMarkdown"].splitlines() if l.startswith("**Area:**")), "")
    dirs = set()
    for m in re.findall(r"`([^`]+)`", line):
        m = m.strip()
        if m.startswith(("backend/", "frontend/", "orchestration-decisions/", "scripts/", "runner/")):
            parts = m.rstrip("/").split("/")
            dirs.add("/".join(parts[:3]) if m.startswith("backend/") else "/".join(parts[:2]))
    if "LeaseEndpoints" in line or "LaneSortApplier" in line:
        dirs.add("backend/Features/Tasks")
    return dirs


areas = {t["id"]: area(t) for t in items}
print("\nAreas:", {k: sorted(v) for k, v in areas.items()})
unordered = []
ids = [t["id"] for t in items]
for i, a in enumerate(ids):
    for b in ids[i + 1:]:
        shared = areas[a] & areas[b]
        if shared and a not in ancestors(b):
            unordered.append((a, b, sorted(shared)))
print("same-area pairs without a dependsOn path:", unordered or "none")
if unordered:
    failures.append("same-area slices unordered")

# 4. Replay table against replay.py output.
out = subprocess.run([sys.executable, "-I", os.path.join(HERE, "replay.py")],
                     capture_output=True, text=True, check=True).stdout
section = out.split("== durations: unit", 1)[1].split("== durations: size", 1)[0]
replayed = {}
for line in section.splitlines():
    if line.startswith('{"name"'):
        r = json.loads(line)
        replayed[r["name"].replace(" (baseline)", "")] = [
            str(r["runs"]), str(r["exposedPairs"]), str(r["cardsNeedingSync"]),
            f'{r["roundsPerCard"]:.2f}', f'{r["parallelism"]:.2f}', f'{r["makespan"]:g}',
            str(r["valuePlain"]), str(r["value"])]
html = open(os.path.join(DOSSIER, "replay.html"), encoding="utf-8").read()
table = html.split('<h3 id="replay-results">', 1)[1].split("</table>", 1)[0]
published = {}
for row in re.findall(r"<tr><td>([^<]+)</td>((?:<td>[^<]*</td>)+)</tr>", table):
    published[row[0]] = re.findall(r"<td>([^<]*)</td>", row[1])
drift = []
for name, cells in replayed.items():
    if published.get(name) != cells:
        drift.append(f"{name}: published {published.get(name)} replayed {cells}")
print("\nreplay table vs replay.py:", "match for %d personas" % len(replayed) if not drift else drift)
if drift or len(replayed) != 6:
    failures.append("replay table drifts from replay.py")
# 5. Wave card parts join to the wave.
wave_path = os.path.join(HERE, "wave-2026-11-17.json")
wave = json.load(open(wave_path, encoding="utf-8"))
joined = []
for number, name in enumerate(wave.get("parts", []), 1):
    part = json.load(open(os.path.join(HERE, name), encoding="utf-8"))
    keys = [c["key"] for c in part["cards"]]
    if (part.get("wave") != wave["id"] or part.get("part") != number or part.get("of") != len(wave["parts"])
            or part.get("first") != keys[0] or part.get("last") != keys[-1]):
        failures.append(f"{name} header does not match the wave or its cards")
    joined += keys
ordered = joined == sorted(joined) and len(set(joined)) == len(joined)
print(f"\nwave parts: {len(wave.get('parts', []))} files, {len(joined)} cards, "
      f"{'distinct and in key order' if ordered else 'NOT distinct or out of order'}")
if not ordered or len(joined) != wave.get("cardCount") or wave.get("cardCount") != 57:
    failures.append("wave parts do not join to the 57 cards")

# 6. Review input: every file reaches the concept review whole.
review = subprocess.run([sys.executable, "-I", os.path.join(DOSSIER, "review_input.py")],
                        capture_output=True, text=True)
problems = [line for line in review.stdout.splitlines() if line.startswith("PROBLEM ")]
summary = next((line for line in review.stdout.splitlines() if line.startswith("review input:")), "review input: no output")
print("\n" + summary)
for line in problems:
    print("  " + line)
if review.returncode != 0:
    failures.append("review input has problems")

print("\nresult:", "PASS" if not failures else "FAIL " + "; ".join(failures))
sys.exit(1 if failures else 0)

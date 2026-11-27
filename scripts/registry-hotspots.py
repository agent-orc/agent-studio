#!/usr/bin/env python3
"""Measure integrated-card contention. Standard library only; never moves Git refs."""
import argparse
import collections
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess

CARD = re.compile(r"\b[A-Z][A-Z0-9]{1,15}-\d+\b(?!-[A-Z0-9])")


def git(repo, *args, allowed=(0,)):
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True)
    if result.returncode not in allowed:
        raise RuntimeError(result.stderr.decode(errors="replace"))
    return result.stdout.decode("utf-8", errors="strict"), result.returncode


def instant(value):
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    return (parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.timezone.utc)).astimezone(dt.timezone.utc)


def history(repo, ref, start, end, all_refs=False):
    raw, _ = git(repo, "log", *( ["--all"] if all_refs else [ref, "--first-parent"]),
                 "--since=" + start.isoformat(), "--until=" + end.isoformat(),
                 "--format=%H%x00%P%x00%cI%x00%B%x00")
    fields = raw.split("\0")
    for i in range(0, len(fields) - 3, 4):
        sha, parents, at, body = fields[i:i + 4]
        if start <= instant(at) < end:
            yield sha.strip(), parents.split(), at, body


def owner(repo, body):
    trailer = re.search(r"^Agent-Studio-Task: (\S+)", body, re.M)
    if trailer:
        return trailer[1]
    subject = body.splitlines()[0]
    if match := CARD.search(subject):
        return match[0]
    if source := re.search(r"(?:Integrate net change from |Agent-Studio-Net-Change-Of: )([a-f0-9]+)", body):
        original, _ = git(repo, "show", "-s", "--format=%B", source[1])
        if original != body:
            return owner(repo, original)
    return None


def integrated(repo, ref, start, end):
    rows = []
    unknown = []
    for sha, parents, at, body in history(repo, ref, start, end):
        if not body.startswith("Integrate ") or not parents:
            continue
        card = owner(repo, body)
        if card is None:
            unknown.append(sha)
            continue
        paths, _ = git(repo, "diff", "--name-only", "-z", parents[0], sha)
        rows.append(dict(card=card, sha=sha, at=at, paths=[p for p in paths.split("\0") if p]))
    return rows, unknown


def recorded_conflicts(root, project, target, start, end):
    """Accept the durable ledger directory or an exported integration-conflicts API response."""
    rows, warnings, identities = [], [], set()
    if root is None or not root.exists():
        return rows, warnings
    for path in sorted(root.rglob("*.json")) if root.is_dir() else [root]:
        try:
            data = json.loads(path.read_text())
            for row in data.get("records", [data]):
                if row.get("project", "").casefold() != project.casefold():
                    continue
                if row.get("integrationTarget") != target or row.get("version") != 1:
                    continue
                if not start <= instant(row["detectedAtUtc"]) < end or row["id"] in identities:
                    continue
                identities.add(row["id"])
                rows.append(dict(id=row["id"], card=row["cardKey"], at=row["detectedAtUtc"],
                                 paths=sorted({f["path"] for f in row["files"]}), source="ledger"))
        except (ValueError, KeyError, TypeError, AttributeError, OSError) as error:
            warnings.append(f"{path}: {error}")
    return rows, warnings


def historical_conflicts(repo, ref, start, end):
    """Replay surviving develop-sync merges. These are recoverable collisions, not attempt totals."""
    rows, seen = [], set()
    for sha, parents, at, body in history(repo, ref, start, end, all_refs=True):
        subject = body.splitlines()[0]
        if len(parents) != 2 or not re.search(r"(?:merge|sync).*develop", subject, re.I):
            continue
        card = owner(repo, body)
        identity = (card, *parents)
        if card is None or identity in seen:
            continue
        seen.add(identity)
        output, code = git(repo, "merge-tree", "--write-tree", "--name-only", "--no-messages", "-z",
                           *parents, allowed=(0, 1))
        if code == 1:
            paths = sorted(set(output.split("\0")[1:]) - {""})
            rows.append(dict(id=sha, card=card, at=at, paths=paths, source="git-replayed-sync"))
    return rows


def rank(rows, distinct_cards=False):
    counts = collections.defaultdict(set)
    for row in rows:
        for path in row["paths"]:
            counts[path].add(row["card"] if distinct_cards else row["id"])
    return [dict(path=path, count=len(values)) for path, values in
            sorted(counts.items(), key=lambda item: (-len(item[1]), item[0]))]


def registry_style(path, text):
    return (path.endswith((".md", ".html")) and
            len(re.findall(r"(?m)^\s*(?:\|[^\n]+\||<tr\b|#{2,3} |<h[23]\b)", text)) >= 6
            or path.endswith(".tsv") and len(text.splitlines()) >= 10
            or path.endswith(".cs") and (
                len(re.findall(r"\b(?:Add\w+|Map\w+)\s*(?:<[^;\n]+>)?\s*\(|\bswitch\b|\btypeof\s*\(", text)) >= 8
                or Path(path).stem.endswith(("Registration", "TransitionAdapter"))
                and len(text.splitlines()) >= 80))


def guard(rows, repo, handled, threshold):
    weeks = collections.defaultdict(list)
    for row in rows:
        at = instant(row["at"])
        monday = (at.date() - dt.timedelta(days=at.weekday())).isoformat()
        weeks[monday].append(row)
    problems = []
    for week, entries in sorted(weeks.items()):
        for hot in rank(entries, distinct_cards=True):
            path = hot["path"]
            file = repo / path
            if hot["count"] > threshold and path not in handled and file.is_file():
                if registry_style(path, file.read_text(errors="replace")):
                    problems.append(f"{path}: {hot['count']} distinct cards in UTC week {week} (limit {threshold}). "
                                    "Generate the inventory, split additions by feature, or register a bounded "
                                    "append-only section in scripts/registry-hotspots.json with its strategy and size.")
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--ref", default="HEAD")
    parser.add_argument("--from", dest="start")
    parser.add_argument("--to", dest="end")
    parser.add_argument("--project", default="Agent Taskboard")
    parser.add_argument("--target", default="develop")
    local_data = Path(os.environ.get("LOCALAPPDATA" if os.name == "nt" else "XDG_DATA_HOME",
                                     str(Path.home() / ".local/share")))
    parser.add_argument("--conflicts", type=Path, default=local_data / "agentstudio/integration-conflicts")
    parser.add_argument("--output", type=Path, default=Path(os.environ.get("JOB_RESULTS_DIR", "results")))
    parser.add_argument("--guard", action="store_true")
    args = parser.parse_args()
    if args.end:
        end = instant(args.end)
    else:
        # Revision time keeps review reproducible, including older checkouts.
        stamp, _ = git(args.repo, "show", "-s", "--format=%cI", args.ref)
        end = instant(stamp.strip()) + dt.timedelta(seconds=1)
    start = instant(args.start) if args.start else end - dt.timedelta(days=7)
    if start >= end:
        parser.error("--from must precede --to (UTC half-open interval)")
    cards, unknown = integrated(args.repo, args.ref, start, end)
    conflicts, warnings = ([], []) if args.guard else recorded_conflicts(
        args.conflicts, args.project, args.target, start, end)
    source = "not-read-for-guard" if args.guard else "ledger"
    if not args.guard and not conflicts:
        source = "git-replayed-sync"
        conflicts = historical_conflicts(args.repo, args.ref, start, end)
    revision, _ = git(args.repo, "rev-parse", args.ref)
    report = dict(start=start.isoformat(), end=end.isoformat(), ref=args.ref, revision=revision.strip(),
                  conflictInput=str(args.conflicts),
                  project=args.project, integratedCards=len({r['card'] for r in cards}),
                  integrationCommits=len(cards), unattributedIntegrations=unknown,
                  touches=rank(cards, True), conflictSource=source, conflicts=rank(conflicts),
                  conflictRecords=conflicts, integrations=cards, warnings=warnings,
                  limitations="Git fallback replays surviving sync merges on all refs; missing and rewritten attempts "
                  "cannot be counted. Sync timestamps are recovery times. No historical attempt denominator is inferred.")
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "registry-hotspots.json").write_text(json.dumps(report, indent=2) + "\n")
    lines = [f"# Registry hotspots: {start.isoformat()} to {end.isoformat()}",
             f"\n{report['integratedCards']} distinct integrated cards, {len(cards)} attributed integration commits.",
             f"\nConflict source: {source}. {report['limitations']}"]
    for title, key in [("Integrated cards touching each file", "touches"), ("Conflicts naming each file", "conflicts")]:
        lines += [f"\n## {title}\n", "| File | Count |", "|---|---:|"]
        lines += [f"| {row['path']} | {row['count']} |" for row in report[key]]
    (args.output / "registry-hotspots.md").write_text("\n".join(lines) + "\n")
    if args.guard:
        config = json.loads((Path(__file__).with_suffix(".json")).read_text())
        handled = {r['path'] for r in config['handled']}
        for manifest in (args.repo / ".agent-studio/append-only-registries").glob("*.json"):
            registration = json.loads(manifest.read_text())
            page = args.repo / registration['path']
            if page.is_file():
                content = page.read_text()
                if (content.count(registration['startMarker']) == 1
                        and content.count(registration['endMarker']) == 1):
                    handled.add(registration['path'])
        problems = guard(cards, args.repo, handled, config['weeklyCardLimit'])
        if problems:
            raise SystemExit("\n".join(problems))
    print(f"{report['integratedCards']} cards; {len(conflicts)} conflicts ({source}); reports: {args.output}")


if __name__ == "__main__":
    main()

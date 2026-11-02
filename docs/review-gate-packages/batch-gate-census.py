#!/usr/bin/env python3
"""Read-only census of the batch gate store of one Agent Studio instance.

Usage: python3 -I batch-gate-census.py <batch-gates folder> [--project NAME]
       [--since YYYY-MM-DD] [--until YYYY-MM-DD]

The folder is <LocalApplicationData>/agentstudio/instances/<id>/batch-gates
(BatchGateStorageRoot.ForInstance). The instance id hashes the content root
and the task repository path, so a moved installation writes to a new
instance folder: run the census on every instance folder that has a
batch-gates directory. The script only reads files. Dates are UTC.
"""
import collections
import json
import os
import statistics
import sys


def load(path):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def day(value):
    return (value or "")[:10]


def in_range(value, since, until):
    d = day(value)
    return bool(d) and (since is None or d >= since) and (until is None or d <= until)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    root = argv[1]
    opts = dict(zip(argv[2::2], argv[3::2]))
    project = opts.get("--project")
    since, until = opts.get("--since"), opts.get("--until")

    manifests = []
    for name in sorted(os.listdir(root)):
        manifest = load(os.path.join(root, name, "manifest.json"))
        if not manifest:
            continue
        if project and (manifest.get("scope") or {}).get("project") != project:
            continue
        if in_range(manifest.get("closedAtUtc"), since, until):
            manifest["_dir"] = os.path.join(root, name)
            manifests.append(manifest)

    sizes = collections.Counter(len(m.get("members") or []) for m in manifests)
    per_day = collections.Counter(day(m.get("closedAtUtc")) for m in manifests)
    splits = sum(1 for m in manifests if m.get("parentBatchId"))
    exclusions = collections.Counter(
        str(e.get("reason")) for m in manifests for e in (m.get("exclusions") or []))
    outcomes = collections.Counter()
    for m in manifests:
        runs = os.path.join(m["_dir"], "runs")
        if os.path.isdir(runs):
            for name in os.listdir(runs):
                if name.endswith(".verdict.json"):
                    verdict = load(os.path.join(runs, name)) or {}
                    outcomes[(verdict.get("outcome"), verdict.get("classification"))] += 1

    print(f"batches closed: {len(manifests)}")
    print("size distribution:", dict(sorted(sizes.items())))
    if manifests:
        mean = sum(k * v for k, v in sizes.items()) / len(manifests)
        print(f"mean size: {mean:.2f}")
    print("per day:", dict(sorted(per_day.items())))
    print(f"split batches (parentBatchId set): {splits}")
    print("exclusion reasons:", dict(exclusions.most_common()))
    print("run verdicts (outcome, classification):", dict(outcomes.most_common()))

    plans = os.path.join(root, "plans")
    if os.path.isdir(plans):
        decisions = collections.Counter()
        for name in sorted(os.listdir(plans)):
            if not name.endswith(".jsonl"):
                continue
            if project and name[:-6] != project:
                continue
            with open(os.path.join(plans, name), encoding="utf-8") as handle:
                for line in handle:
                    try:
                        plan = json.loads(line)
                    except ValueError:
                        continue
                    key = (plan.get("decision"), plan.get("reason"))
                    decisions[key] += 1
        print("plan decisions (decision, reason):")
        for (decision, reason), count in decisions.most_common():
            print(f"  {count:7d}  {decision}: {reason}")

    executions = os.path.join(root, "executions")
    if os.path.isdir(executions):
        durations, waits = [], []
        exec_outcomes = collections.Counter()
        for name in os.listdir(executions):
            fact = load(os.path.join(executions, name)) or {}
            if project and fact.get("project") != project:
                continue
            if not in_range(fact.get("startedAtUtc"), since, until):
                continue
            exec_outcomes[fact.get("outcome")] += 1
            if fact.get("durationMs"):
                durations.append(fact["durationMs"] / 60000)
            if fact.get("queueWaitMs") is not None:
                waits.append(fact["queueWaitMs"] / 60000)
        print("gate executions by outcome:", dict(exec_outcomes))
        if durations:
            print(f"gate minutes: median {statistics.median(durations):.1f}, n={len(durations)}")
        if waits:
            print(f"queue wait minutes: median {statistics.median(waits):.1f}")

    pending = os.path.join(root, "pending")
    if os.path.isdir(pending):
        open_count = sum(1 for n in os.listdir(pending) if n.endswith(".json"))
        resolved = sum(1 for n in os.listdir(pending) if n.endswith(".json.resolved"))
        print(f"pending records: {open_count} open, {resolved} resolved")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

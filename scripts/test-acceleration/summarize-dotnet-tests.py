#!/usr/bin/env python3
"""Summarize dotnet test TRX files or legacy verbose console logs as JSON."""

import argparse
import collections
import datetime as dt
import json
import os
import re
import socket
import xml.etree.ElementTree as ET
from pathlib import Path


CONSOLE_RESULT = re.compile(r"^\s*(Passed|Failed|Skipped)\s+(.+?)\s+\[(< 1|[\d.]+)\s*(ms|s|min)\]$")
ELAPSED = re.compile(r"Time Elapsed:\s*([\d:.]+)")
TOTAL_TIME = re.compile(r"Total time:\s*([\d.]+)\s*(Hours?|Minutes?|Seconds?)", re.I)
TOTAL_TESTS = re.compile(r"^Total tests:\s*(\d+)")


def seconds(value, unit):
    return float(value.replace("< 1", "0.5")) * {"ms": 0.001, "s": 1, "min": 60}[unit]


def trx_tests(path):
    root = ET.parse(path).getroot()
    times = root.find(".//{*}Times")
    wall = None
    if times is not None and times.get("start") and times.get("finish"):
        try:
            start = dt.datetime.fromisoformat(times.get("start").replace("Z", "+00:00"))
            finish = dt.datetime.fromisoformat(times.get("finish").replace("Z", "+00:00"))
            wall = max(0, (finish - start).total_seconds())
        except ValueError:
            pass
    definitions = {}
    for definition in root.findall(".//{*}UnitTest"):
        method = definition.find("{*}TestMethod")
        if method is not None:
            definitions[definition.get("id")] = (method.get("className") or "unknown", method.get("name") or "unknown")
    tests = []
    for result in root.findall(".//{*}UnitTestResult"):
        class_name, method_name = definitions.get(result.get("testId"), ("unknown", result.get("testName") or "unknown"))
        duration = result.get("duration") or "0:00:00"
        parts = duration.split(":")
        elapsed = float(parts[-1]) + 60 * float(parts[-2]) + 3600 * float(parts[-3]) if len(parts) >= 3 else 0
        tests.append({"class": class_name, "name": f"{class_name}.{method_name}", "seconds": elapsed,
                      "outcome": result.get("outcome") or "Unknown"})
    return tests, wall


def console_tests(path):
    tests = []
    wall = None
    declared_count = None
    for line in Path(path).read_text(errors="replace").splitlines():
        match = CONSOLE_RESULT.match(line)
        if match:
            outcome, name, value, unit = match.groups()
            class_name = name.split("(", 1)[0].rsplit(".", 1)[0]
            tests.append({"class": class_name, "name": name, "seconds": seconds(value, unit), "outcome": outcome})
        elapsed = ELAPSED.search(line)
        if elapsed:
            parts = elapsed.group(1).split(":")
            wall = sum(float(part) * 60 ** index for index, part in enumerate(reversed(parts)))
        total_time = TOTAL_TIME.search(line)
        if total_time:
            wall = float(total_time.group(1)) * (3600 if total_time.group(2).lower().startswith("hour") else
                                                 60 if total_time.group(2).lower().startswith("minute") else 1)
        count = TOTAL_TESTS.match(line)
        if count:
            declared_count = int(count.group(1))
    return tests, wall, declared_count


def concurrent_suites():
    count = 0
    if not Path("/proc").is_dir():
        return None
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if (entry / "comm").read_text().strip() != "dotnet":
                continue
            words = (entry / "cmdline").read_bytes().split(b"\0")
            if len(words) > 1 and Path(words[0].decode(errors="replace")).name == "dotnet" and words[1] == b"test":
                count += 1
        except (OSError, PermissionError):
            continue
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--retries", type=int, default=0)
    parser.add_argument("--host-load", type=float)
    parser.add_argument("--concurrent-suites", type=int)
    parser.add_argument("--source-note", default="")
    args = parser.parse_args()
    tests = []
    wall_times = []
    declared_counts = []
    for path in args.inputs:
        if path.suffix.lower() == ".trx":
            parsed, wall = trx_tests(path)
            declared = None
        else:
            parsed, wall, declared = console_tests(path)
        tests.extend(parsed)
        if wall is not None:
            wall_times.append(wall)
        if declared is not None:
            declared_counts.append(declared)
    by_class = collections.defaultdict(float)
    for test in tests:
        by_class[test["class"]] += test["seconds"]
    report = {
        "schemaVersion": 1,
        "recordedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "host": socket.gethostname(),
        "hostLoad1": args.host_load if args.host_load is not None else os.getloadavg()[0],
        "concurrentSuites": args.concurrent_suites if args.concurrent_suites is not None else concurrent_suites(),
        "retries": args.retries,
        "sourceNote": args.source_note,
        "sources": [str(path) for path in args.inputs],
        "total": {"parsedTests": len(tests), "reportedTests": sum(declared_counts) if declared_counts else None,
                  "sumSeconds": round(sum(test["seconds"] for test in tests), 3),
                  "wallSeconds": round(sum(wall_times), 3) if wall_times else None,
                  "failed": sum(test["outcome"].lower() == "failed" for test in tests)},
        "perClassSeconds": dict(sorted(((name, round(value, 3)) for name, value in by_class.items()),
                                       key=lambda item: (-item[1], item[0]))),
        "slowest50": sorted(tests, key=lambda test: -test["seconds"])[:50],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(f"{len(tests)} parsed tests, {report['total']['sumSeconds']:.1f} summed seconds, {report['total']['wallSeconds']} wall seconds")


if __name__ == "__main__":
    main()

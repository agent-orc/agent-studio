"""Replay Codex command starts from a card log without executing any command."""

import argparse
from collections import defaultdict
from datetime import datetime
import json
import os
from pathlib import Path
import runpy
import shlex


GUARD = runpy.run_path(str(Path(__file__).resolve().parents[1] / "runner/StatusGuard/status_guard.py"))


def read_attempts(path):
    attempts = []
    with path.open() as source:
        for line in source:
            frame = line.partition("] [stdout] ")
            if not frame[1]:
                continue
            try:
                event = json.loads(frame[2])
            except ValueError:
                continue
            if event.get("type") == "thread.started":
                attempts.append([])
            item = event.get("item", {})
            if not attempts or event.get("type") != "item.started" or item.get("type") != "command_execution":
                continue
            argv = shlex.split(item["command"])
            if len(argv) == 3 and argv[:2] in (["/bin/bash", "-lc"], ["/bin/bash", "-c"]):
                attempts[-1].append((datetime.fromisoformat(frame[0][1:]).timestamp(), argv[2]))
    return attempts


def split_periods(times, interval):
    period = []
    for timestamp in times:
        if period and timestamp - period[-1] >= interval:
            yield period
            period = []
        period.append(timestamp)
    if period:
        yield period


def replay(calls, interval, cap):
    groups = defaultdict(list)
    for timestamp, command in calls:
        if GUARD["status_tools"](command):
            groups[command].append(timestamp)

    delayed = []
    periods = []
    for timestamps in groups.values():
        for period in split_periods(timestamps, interval):
            if len(period) < 2:
                continue
            first, last = period[0], period[-1]
            previous_arrival = first
            previous_execution = first
            within_window = 1
            for observed_arrival in period[1:]:
                # The next sequential request retains its observed time gap after
                # the preceding result. Other command streams are left unchanged.
                arrival = previous_execution + observed_arrival - previous_arrival
                delay, _ = GUARD["delay_for"](previous_execution, arrival, interval, cap)
                assert 0 < delay <= cap
                delayed.append(delay)
                previous_execution = arrival + delay
                previous_arrival = observed_arrival
                if previous_execution <= last:
                    within_window += 1
            periods.append((len(period), within_window, last - first))

    return {
        "commandCalls": len(calls),
        "eligibleCalls": sum(map(len, groups.values())),
        "delayedCalls": len(delayed),
        "repeatedStatusShare": len(delayed) / len(calls) if calls else 0,
        "totalDelaySeconds": round(sum(delayed), 1),
        "longestDelaySeconds": round(max(delayed, default=0), 1),
        "waitingPeriods": len(periods),
        "callsInSameWaitingPeriodsBefore": sum(before for before, _, _ in periods),
        "callsInSameWaitingPeriodsAfter": sum(after for _, after, _ in periods),
        "busiestWaitingPeriod": dict(zip(("before", "after", "seconds"), max(periods))) if periods else None,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    args = parser.parse_args()
    os.environ["JOB_RESULTS_DIR"] = "/attempt/results"
    attempts = read_attempts(args.log)
    assert attempts and any(attempts), "The log contains no Codex Bash tool starts"
    measured = [
        {"attempt": index, **replay(calls, 180, 8)}
        for index, calls in enumerate(attempts, 1) if calls
    ]
    assert any(row["delayedCalls"] > 0 and
               row["callsInSameWaitingPeriodsAfter"] < row["callsInSameWaitingPeriodsBefore"] for row in measured)
    print(json.dumps(measured, indent=2))

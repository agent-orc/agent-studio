#!/usr/bin/env python3
"""Validate and import the AGT-4375 coding window into a permitted Task Server project."""
import argparse
from collections import Counter, defaultdict
import csv
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import re
from urllib.parse import quote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

CSV_HASH = "6281ccf4c22d1a043882109be45b9c87875fde3d1b2d9ea146ccd6f31f901d4a"
MEASUREMENTS_HASH = "7d49c6350b7bdc0cfae1034118470aae3d7c1c8b18979a56b8e4a636323a123a"
START = "2026-11-27T21:52:00.000"
END = "2026-11-28T09:12:07.229"
FRAME = re.compile(r"^\[([\d:T.Z+-]+)\] \[stdout\] (\{.*)")
THREAD = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
RESPONSE = re.compile(r"resp_[a-zA-Z0-9_-]{1,128}")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def validated(path, digest):
    data = path.read_bytes()
    if sha(data) != digest:
        raise ValueError(f"{path.name} does not match the validated AGT-4375 snapshot")
    return data


def timestamp(value):
    return int(datetime.fromisoformat(value[:23]).replace(tzinfo=timezone.utc).timestamp() * 1000 + 0.5)


def events(path):
    with path.open(encoding="utf-8-sig", errors="replace") as stream:
        for line in stream:
            try:
                yield json.loads(line)
            except ValueError:
                continue


def card_starts(path):
    starts = []
    if not path.is_file():
        return starts
    with path.open(encoding="utf-8-sig", errors="replace") as stream:
        for line in stream:
            match = FRAME.match(line)
            if not match or '"thread.started"' not in match[2]:
                continue
            try:
                event = json.loads(match[2])
            except ValueError:
                continue
            thread = event.get("thread_id", "")
            if event.get("type") == "thread.started" and THREAD.fullmatch(thread):
                starts.append((match[1][:23], thread))
    return starts


def source_turns(path):
    turns = []
    seen = set()
    for event in events(path):
        at = event.get("timestamp", "")[:23]
        if at > END:
            continue
        payload = event.get("payload") or {}
        if event.get("type") == "event_msg" and payload.get("type") == "task_started":
            turns.append((at, []))
        if not turns or event.get("type") != "token_usage_record":
            continue
        response_id = payload.get("response_id")
        if response_id and response_id in seen:
            continue
        if response_id:
            seen.add(response_id)
        usage = payload.get("usage") or {}
        alias = sha(f"codex-response-v1:{response_id}".encode()) if (
            isinstance(response_id, str) and RESPONSE.fullmatch(response_id)) else None
        turns[-1][1].append(
            (at, usage.get("input_tokens"), usage.get("cached_input_tokens", 0), usage.get("output_tokens"), alias))
    return turns


def attach_aliases(rows, run_facts, card_root, rollout_root):
    index = defaultdict(list)
    for path in rollout_root.glob("*/sessions/2026/11/*/rollout-*.jsonl"):
        thread = path.name[-42:-6]
        if THREAD.fullmatch(thread) and path.resolve().is_relative_to(rollout_root):
            index[thread].append(path)

    by_card = defaultdict(dict)
    for (card, run), entries in rows.items():
        by_card[card][run] = entries
    for card, runs in by_card.items():
        path = card_root / card / "logs" / "cli-output.log"
        if not path.resolve().is_relative_to(card_root):
            continue
        starts = card_starts(path)
        sources = {}
        used = set()
        for run, entries in sorted(runs.items()):
            if run > len(starts):
                continue
            start, thread = starts[run - 1]
            if start != run_facts[card, run]["start"] or not index[thread]:
                continue
            if thread not in sources:
                sources[thread] = source_turns(max(index[thread], key=lambda item: item.stat().st_size))
            candidates = []
            for turn, (at, calls) in enumerate(sources[thread]):
                if (thread, turn) in used or abs(timestamp(at) - timestamp(start)) > 120_000:
                    continue
                if all(ordinal <= len(calls) and calls[ordinal - 1][:4] == evidence
                       for ordinal, evidence, _ in entries):
                    candidates.append((turn, calls))
            if len(candidates) != 1:
                continue
            turn, calls = candidates[0]
            used.add((thread, turn))
            for ordinal, _, row in entries:
                alias = calls[ordinal - 1][4]
                if alias:
                    row["responseAlias"] = alias


def prepare(results, card_root=None, rollout_root=None):
    measurement = json.loads(validated(results / "measurements.json", MEASUREMENTS_HASH))
    if (measurement["start"], measurement["end"]) != (START, END):
        raise ValueError("Reference window mismatch")
    data = validated(results / "calls.csv", CSV_HASH)
    cards = {card["key"]: card for card in measurement["cards"]}
    run_facts = {(key, run["n"]): run for key, card in cards.items() for run in card["runs"]}
    rows = defaultdict(list)
    counts = Counter()
    seen = set()
    for entry in csv.DictReader(io.StringIO(data.decode("utf-8"))):
        card, run, ordinal = entry["card"], int(entry["cli_run"]), int(entry["response_in_turn"])
        at = entry["utc"]
        if (card, run, ordinal) in seen or not START < at <= END:
            raise ValueError("Duplicate or out-of-window source row")
        seen.add((card, run, ordinal))
        if cards[card]["mode"] != "coding":
            counts["research"] += 1
            continue
        prompt, cached, output = (int(entry[key]) for key in ("context_input", "cached_input", "output"))
        kind = {"": "work", "wait": "wait", "status": "status"}[entry["only_wait"]]
        facts = run_facts[card, run]
        row = dict(card=card, run=run, ordinal=ordinal, happenedAtMs=timestamp(at), revision=1,
                   kind=kind, model=facts["model"], reasoningLevel=facts["thinking"],
                   promptTokens=prompt, cachedInput=cached, output=output, reasoning=None)
        rows[card, run].append((ordinal, (at, prompt, cached, output), row))
        counts.update(calls=1, tokens=prompt + output)
        if kind != "work":
            counts[kind] += 1
    for key, entries in rows.items():
        facts = run_facts[key]
        totals = [len(entries), *(sum(row[1][field] for row in entries) for field in (1, 2, 3))]
        if totals != [facts[field] for field in ("calls", "input", "cache", "output")]:
            raise ValueError("Per-run control totals disagree")
    counts["cards"] = len({card for card, _ in rows})
    if counts != dict(calls=19774, cards=58, tokens=2639640822, wait=4592, status=924, research=680):
        raise ValueError(f"Reference controls disagree: {counts}")
    if card_root is not None:
        attach_aliases(rows, run_facts, card_root.resolve(), rollout_root.resolve())
    calls = [row for entries in rows.values() for _, _, row in entries]
    counts["codexResponseIds"] = sum("responseAlias" in row for row in calls)
    fingerprint = sha(f"agt-4375-coding-v1:{CSV_HASH}:{MEASUREMENTS_HASH}".encode())
    return dict(fingerprint=fingerprint, calls=calls), dict(counts)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, help="AGT-4375 results directory")
    parser.add_argument("--card-root", type=Path, help="Readable, authorized card shard for source resolution")
    parser.add_argument("--rollout-root", type=Path, help="Readable, authorized Codex clean-context root")
    destination = parser.add_mutually_exclusive_group(required=True)
    destination.add_argument("--prepare", type=Path, help="Write a sanitized request file")
    destination.add_argument("--server", help="Task Server base URL")
    parser.add_argument("--project", help="Target project ID for upload")
    args = parser.parse_args()
    if bool(args.card_root) != bool(args.rollout_root):
        parser.error("--card-root and --rollout-root must be used together")
    target = urlsplit(args.server or "")
    if args.server and (not args.project or not (
            target.scheme == "https" or target.scheme == "http" and target.hostname in ("127.0.0.1", "::1", "localhost"))
            or target.username or target.password or target.query or target.fragment):
        parser.error("Upload requires a project and an HTTPS or loopback HTTP Task Server URL")
    batch, totals = prepare(args.results, args.card_root, args.rollout_root)
    payload = json.dumps(batch, separators=(",", ":")).encode()
    if args.prepare is not None:
        with args.prepare.open("xb") as output:
            output.write(payload)
    else:
        token = os.environ["MODEL_CALL_IMPORT_TOKEN"]
        url = f"{args.server.rstrip('/')}/api/v1/management/model-calls/{quote(args.project, safe='')}/historical-import"
        request = Request(url, payload, {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
            "X-Task-Protocol-Version": "2",
            "X-Task-Client-Version": "1.0.0",
        })
        with build_opener(NoRedirect()).open(request, timeout=120) as response:
            totals["ack"] = json.load(response)
    print(json.dumps({"fingerprint": batch["fingerprint"], **totals}, indent=2))


if __name__ == "__main__":
    main()

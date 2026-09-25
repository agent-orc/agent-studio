#!/usr/bin/env python3
"""Summarize D7 rebase-only receipts from task-local mechanical-round ledgers."""

import argparse
import json
from collections import Counter
from pathlib import Path

TOKEN_CEILING = 1_211_213
DURATION_CEILING = 300
HISTORICAL_REBASE_MEDIAN_TOKENS = 12_112_130
HISTORICAL_REBASE_MEDIAN_SECONDS = 1_258.8
HISTORICAL_REBASE_ROUNDS = 61
HISTORICAL_REBASE_TOKENS = 834_052_877
HISTORICAL_REBASE_QUOTA_POINTS = 22


def field(row, name):
    return row.get(name) if name in row else row.get(name[0].lower() + name[1:])


def usage(receipt, prefix=""):
    values = [field(receipt, prefix + name) for name in ("InputTokens", "OutputTokens", "CacheReadTokens")]
    if all(value is None for value in values):
        return None
    # Codex inputTokens already contains cachedInputTokens; Claude's does not.
    total = (values[0] or 0) + (values[1] or 0)
    if field(receipt, "provider") == "claude":
        total += values[2] or 0
    return total


def load_rows(paths):
    for path in paths:
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue  # interrupted append; a later valid row still counts
            receipt = field(row, "receipt") or {}
            if field(receipt, "mechanicalRebaseRequested"):
                yield path, number, row, receipt


def report(rows):
    resumed = []
    fallback = Counter()
    generation_usage = []
    lines = ["# D7 rebase-only session pilot", "",
             f"Ceilings: {TOKEN_CEILING:,} reported tokens and {DURATION_CEILING} seconds per resumed round.", ""]
    for path, number, row, receipt in rows:
        decision = field(receipt, "resumeDecision") or "unknown"
        reason = field(receipt, "rejectionReason") or "unspecified"
        if decision != "resumed":
            fallback[reason] += 1
        total = usage(receipt)
        if total is not None:
            generation_usage.append(total)
        if decision not in ("resumed", "fallback-fresh"):
            continue
        round_tokens = usage(receipt, "Resume") if decision == "fallback-fresh" else total
        round_seconds = (field(receipt, "resumeDurationSeconds") if decision == "fallback-fresh"
                         else field(receipt, "durationSeconds"))
        resumed.append((field(row, "attemptId"), round_tokens, round_seconds, decision, reason))

    lines += [f"Eligible rebase-only generations: {len(rows)}.",
              f"Resumed rounds: {len(resumed)}. Fresh or fallback generations: {sum(fallback.values())}.", ""]
    if resumed:
        lines += ["| Fenced attempt | Resume tokens | Resume seconds | Against ceiling | Outcome |",
                  "|---|---:|---:|---|---|"]
        for attempt, tokens, seconds, decision, reason in resumed:
            token_text = f"{tokens:,}" if tokens is not None else "unknown"
            second_text = f"{seconds:.1f}" if seconds is not None else "unknown"
            against = ("pass" if tokens is not None and seconds is not None
                       and tokens <= TOKEN_CEILING and seconds <= DURATION_CEILING else "unproven or exceeded")
            outcome = "continued" if decision == "resumed" else f"fresh fallback: {reason}"
            lines.append(f"| {attempt} | {token_text} | {second_text} | {against} | {outcome} |")
        lines.append("")
    if fallback:
        lines += [f"Fallback rate: {sum(fallback.values())}/{len(rows)} "
                  f"({sum(fallback.values()) / len(rows):.1%}). Reasons: "
                  + ", ".join(f"{reason} {count}" for reason, count in sorted(fallback.items())) + ".", ""]
    elif not rows:
        lines += ["Fallback rate and reasons: unmeasured (no eligible rounds).", ""]
    if generation_usage and len(generation_usage) == len(rows):
        average = sum(generation_usage) / len(generation_usage)
        points = max(0, HISTORICAL_REBASE_MEDIAN_TOKENS - average) * HISTORICAL_REBASE_ROUNDS
        points *= HISTORICAL_REBASE_QUOTA_POINTS / HISTORICAL_REBASE_TOKENS
        lines += [f"Observed mean generation usage: {average:,.0f} tokens. "
                  f"Counterfactual for 61 comparable historical rounds: {points:.1f} quota points; "
                  "the Dossier target is 17.6 to 19.8 points. This extrapolation includes fresh fallback usage "
                  "and is not an observed weekly quota saving.", ""]
    else:
        lines += ["Quota-point result: unmeasured. The 17.6 to 19.8 point estimate remains an unvalidated target; "
                  "missing provider usage summaries or no eligible rounds prevent extrapolation.", ""]
    lines += [f"Historical cold rebase-only median: {HISTORICAL_REBASE_MEDIAN_TOKENS:,} tokens "
              f"and {HISTORICAL_REBASE_MEDIAN_SECONDS:,.1f} seconds. "
              "Option A and platform rebase savings overlap and are never added.", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, help="Task-store root to search for ledgers")
    parser.add_argument("--ledger", type=Path, action="append", default=[])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    paths = list(args.ledger)
    if args.root:
        paths.extend(args.root.rglob("mechanical-round-ledger.jsonl"))
    unique = sorted(set(paths))
    rows = list(load_rows(unique))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(report(rows), encoding="utf-8")
    print(f"{len(rows)} eligible generations from {len(unique)} ledgers; report: {args.output}")


if __name__ == "__main__":
    main()

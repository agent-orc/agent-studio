#!/usr/bin/env python3
"""Reproduce paired first-paint and first-detail bootstrap intervals.

Usage: python3 frontend/e2e/perf/paired-bootstrap.py browser-timing-paired-15.json
Positive differences mean the candidate is faster than the baseline.
"""

import argparse
import json
import random
import statistics
from pathlib import Path


def percentile(sorted_values: list[float], fraction: float) -> float:
    position = (len(sorted_values) - 1) * fraction
    low = int(position)
    weight = position - low
    return sorted_values[low] * (1 - weight) + sorted_values[min(low + 1, len(sorted_values) - 1)] * weight


def interval(differences: list[float], seed: int, resamples: int) -> tuple[float, float]:
    rng = random.Random(seed)
    medians = sorted(statistics.median(rng.choices(differences, k=len(differences))) for _ in range(resamples))
    return percentile(medians, 0.025), percentile(medians, 0.975)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('samples', type=Path, help='JSON output from board-bundle-lazy.spec.ts')
    parser.add_argument('--resamples', type=int, default=100_000)
    args = parser.parse_args()
    if args.resamples < 1:
        parser.error('--resamples must be positive')

    samples = json.loads(args.samples.read_text())
    pairs: dict[int, dict[str, dict]] = {}
    for sample in samples:
        if sample['count'] != 20:
            continue
        pair = pairs.setdefault(sample['pair'], {})
        phase = sample['phase']
        if phase in pair:
            parser.error(f'duplicate {phase} sample for pair {sample["pair"]}')
        pair[phase] = sample
    if not pairs or any(set(pair) != {'before', 'after'} for pair in pairs.values()):
        parser.error('every 20-card pair must have one before and one after sample')

    result = {'pairs': len(pairs), 'resamples': args.resamples, 'metrics': {}}
    for metric, seed in [('firstPaintMs', 3539), ('detailOpenMs', 3542)]:
        differences = [pair['before'][metric] - pair['after'][metric] for _, pair in sorted(pairs.items())]
        low, high = interval(differences, seed, args.resamples)
        result['metrics'][metric] = {
            'seed': seed,
            'pairedMedianGainMs': statistics.median(differences),
            'confidence95Ms': [low, high],
        }
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()

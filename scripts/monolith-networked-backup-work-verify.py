#!/usr/bin/env python3
"""Compare the stores the API reopens before and after a restored stack starts."""
import argparse
import json
import pathlib
import struct
import sys


def authority(root):
    path = root / 'workspace/.metadata/attempt-authority.json'
    if not path.exists():
        return {'runs': {}, 'reviews': {}}
    state = json.loads(path.read_text())
    runs = {row['attemptId']: row for row in state.get('runAttempts', [])}
    reviews = {row['attemptId']: row for row in state.get('reviewAttempts', [])}
    journal = pathlib.Path(str(path) + '.journal')
    if journal.exists():
        with journal.open('rb') as stream:
            while header := stream.read(36):
                if len(header) != 36:
                    raise ValueError('truncated attempt authority journal header')
                size = struct.unpack_from('<i', header)[0]
                payload = stream.read(size)
                if len(payload) != size:
                    raise ValueError('truncated attempt authority journal entry')
                delta = json.loads(payload)
                for row in delta.get('changedRuns', []):
                    runs[row['attemptId']] = row
                for row in delta.get('changedReviews', []):
                    reviews[row['attemptId']] = row
                for patch in delta.get('runPatches', []):
                    runs[patch['attemptId']].update(patch['fields'])
                for patch in delta.get('reviewPatches', []):
                    reviews[patch['attemptId']].update(patch['fields'])
                for item in delta.get('removedRunIds', []):
                    runs.pop(item, None)
                for item in delta.get('removedReviewIds', []):
                    reviews.pop(item, None)
    return {'runs': runs, 'reviews': reviews}


def inventory(root):
    result = authority(root)
    batches = root / 'home/.local/share/agentstudio'
    result['batches'] = {}
    if batches.exists():
        for manifest_path in batches.glob('**/manifest.json'):
            if 'batch-gates' not in manifest_path.parts:
                continue
            manifest = json.loads(manifest_path.read_text())
            # BatchGateStore writes the record itself, with optional envelope metadata.
            manifest = manifest.get('payload', manifest)
            batch_id = manifest['batchId']
            publication = manifest_path.parent / 'publication.json'
            red = any(json.loads(path.read_text()).get('outcome') in ('fail', 'red')
                      for path in (manifest_path.parent / 'runs').glob('*.verdict.json'))
            result['batches'][batch_id] = {
                'subjects': sorted(member['taskKey'] for member in manifest['members']),
                'published': publication.exists(),
                'red': red,
            }
    return result


def verify(before, after, require):
    checks = {'runAttempts': len(before['runs']), 'reviewAttempts': len(before['reviews']),
              'batches': len(before['batches']), 'publications': sum(b['published'] for b in before['batches'].values())}
    if require and not all(checks.values()):
        raise ValueError('running-team proof needs coding, review, batch and publication records in restored stores')
    if require and not any(batch['red'] for batch in before['batches'].values()):
        raise ValueError('running-team proof needs a red batch in restored stores')
    active = [(kind, row) for kind in ('runs', 'reviews') for row in before[kind].values()
              if row.get('state') in (0, 1, 'Pending', 'Leased')]
    if require and not any(kind == 'runs' for kind, _ in active):
        raise ValueError('running-team proof needs an active coding attempt')
    if require and not any(kind == 'reviews' for kind, _ in active):
        raise ValueError('running-team proof needs an active review attempt')
    for kind in ('runs', 'reviews'):
        for attempt_id, row in before[kind].items():
            if attempt_id not in after[kind]:
                raise ValueError(f'{kind} attempt {attempt_id} disappeared after restart')
            next_row = after[kind][attempt_id]
            if next_row.get('lastFence') != row.get('lastFence'):
                raise ValueError(f'{kind} attempt {attempt_id} was claimed with a second fence')
            if require and (kind, row) in active and next_row.get('state') in (0, 1, 'Pending', 'Leased'):
                old_lease = row.get('lease') or {}
                new_lease = next_row.get('lease') or {}
                if new_lease.get('lastHeartbeat') == old_lease.get('lastHeartbeat'):
                    raise ValueError(f'{kind} attempt {attempt_id} neither resumed nor settled after restart')
            subject = row['taskKey']
            old_ids = {key for key, value in before[kind].items() if value['taskKey'] == subject}
            new_ids = {key for key, value in after[kind].items() if value['taskKey'] == subject}
            if new_ids != old_ids:
                raise ValueError(f'{kind} for {subject} claimed again: {sorted(new_ids - old_ids)}')
    for batch_id, old in before['batches'].items():
        new = after['batches'].get(batch_id)
        if (new is None or new['subjects'] != old['subjects']
                or (old['published'] and not new['published']) or (old['red'] and not new['red'])):
            raise ValueError(f'batch {batch_id} changed identity or lost its publication')
    published_subjects = {}
    for batch_id, batch in after['batches'].items():
        if batch['published']:
            for subject in batch['subjects']:
                if subject in published_subjects:
                    raise ValueError(f'{subject} published in both {published_subjects[subject]} and {batch_id}')
                published_subjects[subject] = batch_id
    return checks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('before', type=pathlib.Path)
    parser.add_argument('after', type=pathlib.Path)
    parser.add_argument('--require-running-work', action='store_true')
    args = parser.parse_args()
    before, after = inventory(args.before), inventory(args.after)
    print(json.dumps({'schema': 'agent-studio.restored-work-check/v1',
                      'before': before, 'after': after,
                      'checked': verify(before, after, args.require_running_work)}, sort_keys=True))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, json.JSONDecodeError) as error:
        print(f'restored work check failed: {error}', file=sys.stderr)
        sys.exit(1)

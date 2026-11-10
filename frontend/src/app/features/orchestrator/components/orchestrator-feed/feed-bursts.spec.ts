import { describe, expect, it } from 'vitest';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';
import { buildFeedRows, feedCause, feedCauseLabel } from './feed-bursts';

const STALL = 'Card had no active Auto Review owner beyond the configured threshold.';

function escalation(key: string, second: number, project = 'Demo App'): OrchestratorLogEntry {
  return {
    ts: `2026-11-09T09:23:${String(second).padStart(2, '0')}Z`,
    kind: 'alert',
    topic: 'stall-intervention',
    summary: `AI Operator escalated auto-review:${key}: ${STALL}`,
    project,
    watchPath: `/tmp/${project}`,
    jobId: `${key.toLowerCase()}-slug`,
  };
}

const key = (entry: OrchestratorLogEntry) => `${entry.ts}|${entry.summary}`;

describe('feed bursts (AGT-3585)', () => {
  it('names the shared cause without the card it was written for', () => {
    expect(feedCauseLabel(escalation('PLAT-20', 1))).toBe(`AI Operator escalated auto-review: ${STALL}`);
    expect(feedCauseLabel({ ...escalation('PLAT-20', 1), summary: 'Requeued plat-20-slug after a stall' }))
      .toBe('Requeued after a stall');
  });

  it('never groups decisions or entries without a card', () => {
    expect(feedCause({ ...escalation('DEMO-1', 1), kind: 'decision' })).toBeNull();
    expect(feedCause({ ...escalation('DEMO-1', 1), jobId: null })).toBeNull();
    expect(feedCause(escalation('DEMO-1', 1))).toBe(feedCause(escalation('PLAT-9', 2, 'Demo Platform')));
  });

  it('folds same-cause entries into one row and leaves the rest as single rows (R3)', () => {
    const entries = [
      escalation('DEMO-3', 5),
      { ...escalation('DEMO-9', 4), summary: 'Pipeline health: post-processing stalled' },
      escalation('PLAT-2', 3, 'Demo Platform'),
      { ...escalation('DEMO-7', 2), kind: 'decision' as const },
      escalation('DEMO-1', 1),
    ];
    const rows = buildFeedRows(entries, key, 'Workspace');
    expect(rows.map(row => row.burst ? `burst:${row.burst.entries.length}` : row.entry!.jobId)).toEqual([
      'burst:3', 'demo-9-slug', 'demo-7-slug',
    ]);
    const burst = rows[0].burst!;
    expect(burst.cards).toBe(3);
    expect(burst.newestTs).toBe(entries[0].ts);
    expect(burst.oldestTs).toBe(entries[4].ts);
    expect(burst.projects).toEqual(['Demo App', 'Demo Platform']);
    expect(rows.reduce((sum, row) => sum + (row.burst ? row.burst.entries.length : 1), 0)).toBe(entries.length);
  });
});

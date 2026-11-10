import { describe, expect, it } from 'vitest';
import {
  attentionActionLabel,
  attentionAge,
  attentionClassOf,
  attentionTarget,
  groupAttention,
  type OperatorAttentionItem,
} from './operator-attention.model';

function item(type: string, overrides: Partial<OperatorAttentionItem> = {}): OperatorAttentionItem {
  return {
    id: `${type}|${overrides.cardKey ?? ''}`, type, project: 'alpha', cardKey: 'AGT-1', cards: ['AGT-1'],
    reason: 'Reason.', observedAt: '2026-11-09T09:00:00Z', suggestedOperations: [], ...overrides,
  };
}

describe('operator attention model', () => {
  it('maps every decision-event class of the stream and keeps unknown types visible', () => {
    expect(attentionClassOf('decision-card.opened')).toBe('decision');
    expect(attentionClassOf('escalation.opened')).toBe('escalation');
    expect(attentionClassOf('human-review.ready')).toBe('human-review');
    expect(attentionClassOf('batch.red')).toBe('batch');
    expect(attentionClassOf('batch.running-without-gate')).toBe('batch');
    expect(attentionClassOf('batch.isolating')).toBe('batch');
    expect(attentionClassOf('push.pending-overdue')).toBe('push');
    expect(attentionClassOf('quota.exhausted')).toBe('quota');
    expect(attentionClassOf('quota.fallback-active')).toBe('quota');
    expect(attentionClassOf('backend.stalled')).toBe('host');
    expect(attentionClassOf('progress.idle-with-ready')).toBe('host');
    expect(attentionClassOf('something.new')).toBe('other');
  });

  it('groups in class order and the group sizes add up to the rows (R3)', () => {
    const items = [
      item('human-review.ready', { cardKey: 'AGT-2' }),
      item('backend.stalled', { cardKey: null, cards: [], project: null }),
      item('decision-card.opened', { cardKey: 'AGT-3' }),
      item('human-review.ready', { cardKey: 'AGT-4' }),
      item('something.new'),
    ];
    const groups = groupAttention(items);
    expect(groups.map(group => group.spec.key)).toEqual(['decision', 'human-review', 'host', 'other']);
    expect(groups.reduce((sum, group) => sum + group.items.length, 0)).toBe(items.length);
  });

  it('offers one action per row and falls back to Activity when the target is missing', () => {
    const [review] = groupAttention([item('human-review.ready')]);
    expect(attentionActionLabel(review.items[0], review.spec)).toBe('Review');
    const [batch] = groupAttention([item('batch.red', { cardKey: null, cards: ['A', 'B'] })]);
    expect(attentionTarget(batch.items[0], batch.spec)).toBe('batch-gate');
    const orphan = item('escalation.opened', { cardKey: null, cards: [] });
    const [escalation] = groupAttention([orphan]);
    expect(attentionTarget(orphan, escalation.spec)).toBe('activity');
    expect(attentionActionLabel(orphan, escalation.spec)).toBe('Open Activity');
  });

  it('renders a compact age', () => {
    const now = Date.parse('2026-11-09T12:00:00Z');
    expect(attentionAge('2026-11-09T11:59:40Z', now)).toBe('just now');
    expect(attentionAge('2026-11-09T11:48:00Z', now)).toBe('12 min');
    expect(attentionAge('2026-11-09T09:00:00Z', now)).toBe('3 h');
    expect(attentionAge('2026-11-06T12:00:00Z', now)).toBe('3 d');
    expect(attentionAge('not a date', now)).toBe('');
  });
});

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
  it('puts an open environment incident first and opens its card', () => {
    const incident = item('environment.incident-open', { title: 'alpha · Production incident' });
    const groups = groupAttention([item('decision-card.opened'), incident]);
    expect(groups[0].spec.key).toBe('incident');
    expect(attentionTarget(incident, groups[0].spec)).toBe('card');
  });
  it('maps every decision-event class of the stream and keeps unknown types visible', () => {
    expect(attentionClassOf('decision-card.opened')).toBe('decision');
    expect(attentionClassOf('escalation.opened')).toBe('escalation');
    expect(attentionClassOf('human-review.ready')).toBe('human-review');
    expect(attentionClassOf('batch.red')).toBe('batch');
    expect(attentionClassOf('batch.running-without-gate')).toBe('batch');
    expect(attentionClassOf('batch.isolating')).toBe('batch');
    expect(attentionClassOf('batch.record-unreadable')).toBe('batch-record');
    expect(attentionClassOf('batch-gate.stuck')).toBe('batch');
    expect(attentionClassOf('push.pending-overdue')).toBe('push');
    expect(attentionClassOf('push.blocked')).toBe('push-blocked');
    expect(attentionClassOf('quota.exhausted')).toBe('quota');
    expect(attentionClassOf('quota.fallback-active')).toBe('quota');
    expect(attentionClassOf('host.disk-pressure')).toBe('disk');
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
    const [stuck] = groupAttention([item('batch-gate.stuck', { cardKey: null, cards: ['A', 'B'] })]);
    expect(attentionActionLabel(stuck.items[0], stuck.spec)).toBe('Open batch panel');
    const orphan = item('escalation.opened', { cardKey: null, cards: [] });
    const [escalation] = groupAttention([orphan]);
    expect(attentionTarget(orphan, escalation.spec)).toBe('activity');
    expect(attentionActionLabel(orphan, escalation.spec)).toBe('Open Activity');
  });

  it('opens the environment settings for an unreachable gate host (AGT-3792)', () => {
    const outage = item('gate-host.unreachable', { cardKey: null, cards: ['AGT-3772', 'AGT-3789'], project: null });
    expect(attentionClassOf(outage.type)).toBe('gate-host');
    const [group] = groupAttention([outage]);
    expect(group.spec.label).toBe('Gate host unreachable');
    expect(attentionTarget(group.items[0], group.spec)).toBe('hosts');
    expect(attentionActionLabel(group.items[0], group.spec)).toBe('Open environment');
  });

  it('opens the project Operations view while post-merge runs are missing (AGT-3965)', () => {
    const missing = item('post-merge.missing', { cardKey: null, cards: [], project: 'Agent Studio' });
    expect(attentionClassOf(missing.type)).toBe('post-merge');
    const [group] = groupAttention([missing]);
    expect(group.spec.label).toBe('Post-merge runs missing');
    expect(attentionTarget(group.items[0], group.spec)).toBe('operations');
    expect(attentionActionLabel(group.items[0], group.spec)).toBe('Open operations');
  });

  it('opens the repair card when a red develop incident asks the owner (RT-1)', () => {
    const red = item('post-merge.red-incident', { cardKey: 'AGT-4100', cards: ['AGT-4100'], project: 'Agent Studio' });
    expect(attentionClassOf(red.type)).toBe('red-develop');
    const [group] = groupAttention([red]);
    expect(group.spec.label).toBe('Develop red for two hours');
    expect(attentionTarget(group.items[0], group.spec)).toBe('card');
    expect(attentionActionLabel(group.items[0], group.spec)).toBe('Open repair card');
  });

  it('opens the card of a blocked push (AGT-3695)', () => {
    const [blocked] = groupAttention([item('push.blocked')]);
    expect(blocked.spec.label).toBe('Push blocked');
    expect(attentionTarget(blocked.items[0], blocked.spec)).toBe('card');
    expect(attentionActionLabel(blocked.items[0], blocked.spec)).toBe('Open card');
  });

  it('opens the batch gate for an unreadable batch record, and Activity when it names no project', () => {
    const [named] = groupAttention([item('batch.record-unreadable', { cardKey: null, cards: [] })]);
    expect(named.spec.label).toBe('Batch record unreadable');
    expect(attentionTarget(named.items[0], named.spec)).toBe('batch-gate');
    const unnamed = item('batch.record-unreadable', { cardKey: null, cards: [], project: null });
    expect(attentionTarget(unnamed, named.spec)).toBe('activity');
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

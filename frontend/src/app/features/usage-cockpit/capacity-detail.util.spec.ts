import { describe, expect, it } from 'vitest';

import { buildCapacityDetailView } from './capacity-detail.util';
import type { CapacityQueuedCard } from './models/operator-capacity.model';

const NOW = Date.parse('2026-09-25T14:58:00Z');
const NOTE = 'Not offered by the installed claude-code 2.1.300.';

function card(cardKey: string, outcome: string, overrides: Partial<CapacityQueuedCard> = {}): CapacityQueuedCard {
  return {
    project: 'sample', cardKey, title: `Card ${cardKey}`,
    configuredCli: 'claude', configuredModel: 'claude-opus-5', configuredThinkingLevel: 'high',
    outcome, targetCli: outcome === 'fallback' ? 'codex' : 'claude',
    targetModel: outcome === 'fallback' ? 'gpt-6-sol' : 'claude-opus-5', targetThinkingLevel: 'high',
    reason: 'Weekly window at 97%', nextResetAt: '2026-09-25T18:30:00Z', ...overrides,
  };
}

describe('buildCapacityDetailView', () => {
  const offered = () => null;

  it('lists waits first, then fallbacks, with the target only where the route changes', () => {
    const view = buildCapacityDetailView({ queuedCards: [
      card('SAMPLE-2', 'fallback'), card('SAMPLE-1', 'fallback'), card('SAMPLE-3', 'wait'),
    ] }, 'Europe/Berlin', NOW, offered);

    expect(view.cards.map(c => [c.cardKey, c.outcomeLabel, c.tone, c.target])).toEqual([
      ['SAMPLE-3', 'Would wait', 'warning', null],
      ['SAMPLE-1', 'Would fall back', 'info', 'codex / gpt-6-sol / high'],
      ['SAMPLE-2', 'Would fall back', 'info', 'codex / gpt-6-sol / high'],
    ]);
    expect(view.cards[1].configured).toBe('claude / claude-opus-5 / high');
    expect(view.cards[1].resetLabel).toBe('Returns to configured route');
    expect(view.cards[0].resetLabel).toBe('Retries');
    expect(view.cards[0].reset?.utc).toBe('Fri 25 Sept, 18:30 UTC');
    expect(view.cards[0].countdown).toBe('in 3 h 32 min');
    // R3: the summary counts equal the visible rows.
    expect(view.summary).toBe('1 would wait, 2 would fall back');
  });

  it('flags a configured model the installed CLI does not offer', () => {
    const view = buildCapacityDetailView({ queuedCards: [card('SAMPLE-4', 'fallback', { configuredModel: 'claude-opus-4-8' })] },
      'UTC', NOW, (cli, model) => (cli === 'claude' && model === 'claude-opus-4-8' ? NOTE : null));
    expect(view.cards[0].notOffered).toBe(NOTE);
  });

  it('says so when no Ready card would change route, and reads missing values as Unknown', () => {
    expect(buildCapacityDetailView({}, 'UTC', NOW, offered).summary)
      .toBe('Every Ready card would run its configured model at the current quota.');
    const view = buildCapacityDetailView({ queuedCards: [card('SAMPLE-5', 'throttle', {
      configuredThinkingLevel: null, nextResetAt: null,
    })] }, 'UTC', NOW, offered);
    expect(view.cards[0]).toMatchObject({
      outcomeLabel: 'Would be held', configured: 'claude / claude-opus-5 / Unknown', reset: null, countdown: null,
    });
  });
});

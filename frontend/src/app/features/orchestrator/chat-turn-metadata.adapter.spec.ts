import { describe, expect, it } from 'vitest';
import { summarizeChatMetadata, supportsTurnMetadata, toTurnMetadata } from './chat-turn-metadata.adapter';

describe('chat turn metadata adapter', () => {
  it('gates library metadata at coding-agent-chat 0.5.0', () => {
    expect(supportsTurnMetadata('0.4.1')).toBe(false);
    expect(supportsTurnMetadata('0.5.0')).toBe(true);
    expect(supportsTurnMetadata('0.6.0')).toBe(true);
  });

  it('maps a codex receipt with queue time, execution time and capabilities', () => {
    expect(toTurnMetadata({
      cliType: 'codex', model: 'gpt-6-astra', effort: 'medium',
      providerSessionId: 'thread-1', executingHost: 'agent-runner-01',
      queuedAt: '2026-09-27T12:00:00Z', startedAt: '2026-09-27T12:00:02Z',
      finishedAt: '2026-09-27T12:00:05Z', inputTokens: 900,
      cachedInputTokens: 100, outputTokens: 80, reasoningTokens: 20,
      cost: 0.003, currency: 'USD',
    })).toEqual({
      model: 'gpt-6-astra', effort: 'medium', inputTokens: 900,
      cachedInputTokens: 100, outputTokens: 80, reasoningTokens: 20,
      cacheCreationTokens: undefined, cost: 0.003, currency: 'USD',
      queueMs: 2000, durationMs: 3000, providerSessionId: 'thread-1',
      executingHost: 'agent-runner-01',
      capabilities: { model: true, tokens: true, cost: true, latency: true, providerSessionId: true },
    });
  });

  it('keeps duration when a mode reports no usage', () => {
    const metadata = toTurnMetadata({
      cliType: 'other', queuedAt: '2026-09-27T12:00:00Z',
      startedAt: '2026-09-27T12:00:01Z', finishedAt: '2026-09-27T12:00:03Z',
    });
    expect(metadata?.durationMs).toBe(2000);
    expect(metadata?.capabilities.tokens).toBe(false);
    expect(metadata?.capabilities.cost).toBe(false);
  });

  it('summarizes only the visible receipts without counting reasoning twice', () => {
    expect(summarizeChatMetadata([{ metadata: {
      model: 'gpt-6-astra', queuedAt: '2026-09-27T12:00:00Z',
      finishedAt: '2026-09-27T12:00:03Z', inputTokens: 900,
      cachedInputTokens: 100, outputTokens: 80, reasoningTokens: 20,
      cost: 0.003,
    } }])).toBe('gpt-6-astra · 1,080 tokens · $0.0030 · 3s');
  });
});

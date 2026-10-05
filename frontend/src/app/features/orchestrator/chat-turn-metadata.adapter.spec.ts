import { describe, expect, it } from 'vitest';
import { adaptChatTurnMetadata, chatMetadataLibraryEnabled } from './chat-turn-metadata.adapter';

describe('chat turn metadata adapter', () => {
  it('enables the library binding with the published 0.5.0 pin', () => {
    expect(chatMetadataLibraryEnabled('0.4.1')).toBe(false);
    expect(chatMetadataLibraryEnabled('0.5.0')).toBe(true);
    expect(chatMetadataLibraryEnabled('1.0.0')).toBe(true);
  });

  it('maps usage, price, provider identity and queue plus run durations', () => {
    expect(adaptChatTurnMetadata({
      model: 'gpt-6-astra', effort: 'medium', providerThreadId: 'thread-1', host: 'runner-01',
      queuedAt: '2026-09-26T09:00:00Z', startedAt: '2026-09-26T09:00:03Z',
      finishedAt: '2026-09-26T09:00:07Z', inputTokens: 10, cachedInputTokens: 20,
      outputTokens: 5, reasoningTokens: 2, cost: 0.0001, currency: 'USD',
      priceCatalogueVersion: 'TokenEconomy/0.3.5',
    })).toEqual({
      metadata: {
        model: 'gpt-6-astra', effort: 'medium', providerThreadId: 'thread-1', host: 'runner-01',
        inputTokens: 10, cachedInputTokens: 20, outputTokens: 5, reasoningTokens: 2,
        cost: 0.0001, currency: 'USD', priceCatalogueVersion: 'TokenEconomy/0.3.5',
        queueDurationMs: 3000, runDurationMs: 4000,
      },
      capabilities: {
        tokens: true, cost: true, reasoningTokens: true, queueDuration: true,
        runDuration: true, providerThreadId: true,
      },
    });
  });
});

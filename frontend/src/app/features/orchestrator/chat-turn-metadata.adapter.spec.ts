import { summarizeChatTurns, supportsLibraryTurnMetadata, toLibraryTurnMetadata } from './chat-turn-metadata.adapter';
import type { OrchestratorChatTurn } from './models/orchestrator.model';

describe('chat turn metadata adapter', () => {
  const codex: OrchestratorChatTurn = {
    id: 'reply-1', ts: '2026-09-27T12:00:00Z', role: 'orchestrator', text: 'Done',
    metadata: {
      cliType: 'codex', model: 'gpt-6-astra', effort: 'medium',
      providerSessionId: 'thread-1', host: 'agent-runner-01',
      queueMs: 1800, durationMs: 3200, inputTokens: 100,
      cachedInputTokens: 900, outputTokens: 50, reasoningTokens: 20,
      cost: 0.0042, currency: 'USD', priceCatalogueVersion: 'TokenEconomy 0.3.5',
      usageClass: 'chat-turn',
    },
  };

  it('maps the DTO and mode capabilities without counting cache twice', () => {
    const result = toLibraryTurnMetadata(codex);
    expect(result?.metadata).toEqual(expect.objectContaining({
      providerSessionId: 'thread-1', queueMs: 1800, durationMs: 3200,
      inputTokens: 100, cachedInputTokens: 900, outputTokens: 50,
      reasoningTokens: 20, cost: 0.0042,
    }));
    expect(result?.capabilities).toEqual({
      model: true, tokens: true, cost: true, durations: true,
      providerSessionId: true, host: true,
    });
    expect(summarizeChatTurns([codex])).toEqual({
      tokens: 1050, cost: 0.0042, pricedTurns: 1, turns: 1,
      elapsedMs: 5000, queueMs: 1800, models: ['gpt-6-astra'], providerSessionIds: ['thread-1'],
    });
  });

  it('limits modes without usage to durations', () => {
    const result = toLibraryTurnMetadata({ ...codex, metadata: {
      cliType: 'other', queueMs: 100, durationMs: 500,
    } });
    expect(result?.capabilities.tokens).toBe(false);
    expect(result?.capabilities.cost).toBe(false);
    expect(result?.capabilities.durations).toBe(true);
  });

  it('activates the library binding at version 0.5.0', () => {
    expect(supportsLibraryTurnMetadata('0.4.1')).toBe(false);
    expect(supportsLibraryTurnMetadata('0.5.0')).toBe(true);
    expect(supportsLibraryTurnMetadata('1.0.0')).toBe(true);
  });
});

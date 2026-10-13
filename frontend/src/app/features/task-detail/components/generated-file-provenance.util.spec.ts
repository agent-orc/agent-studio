import { describe, expect, it } from 'vitest';
import { generatedFileProvenance } from './generated-file-provenance.util';

describe('generatedFileProvenance', () => {
  it('formats a compact label and detailed tooltip for generated files', () => {
    const view = generatedFileProvenance({
      file: 'status.md',
      kind: 'status',
      model: 'claude-haiku-4-5',
      cli: 'claude',
      tokensIn: 1200,
      tokensOut: 300,
      tokensTotal: 1500,
      startedAt: '2026-06-09T11:59:58Z',
      endedAt: '2026-06-09T12:00:00Z',
      durationMs: 2000,
      stepId: 'summary-generation',
      runIndex: 2,
      headShaAfter: 'abcdef0123456789',
    });

    expect(view?.label).toBe('claude / claude-haiku-4-5 | 1.5k tokens | 2s');
    expect(view?.tooltip).toContain('File: status.md');
    expect(view?.tooltip).toContain('Step: summary-generation');
    expect(view?.tooltip).toContain('Run: #2');
    expect(view?.tooltip).toContain('Tokens: 1.5k total (1.2k in, 300 out)');
    expect(view?.tooltip).toContain('Commit: abcdef012345');
  });

  it('exposes structured fields for the integrated header provenance', () => {
    const view = generatedFileProvenance({
      file: 'status.md',
      kind: 'status',
      model: 'claude-haiku-4-5',
      cli: 'claude',
      tokensIn: 1200,
      tokensOut: 300,
      tokensTotal: 1500,
      durationMs: 2000,
    });

    expect(view?.producer).toBe('claude / claude-haiku-4-5');
    expect(view?.model).toBe('claude-haiku-4-5');
    expect(view?.cli).toBe('claude');
    expect(view?.tokens).toBe('1.5k tokens');
    expect(view?.duration).toBe('2s');
  });

  it('nulls the token/duration chips when not recorded', () => {
    const view = generatedFileProvenance({
      file: 'status.md',
      kind: 'status',
      model: 'claude-haiku-4-5',
      cli: 'claude',
      tokensIn: 0,
      tokensOut: 0,
      tokensTotal: 0,
      durationMs: 0,
    });

    expect(view?.tokens).toBeNull();
    expect(view?.duration).toBeNull();
    expect(view?.label).toBe('claude / claude-haiku-4-5');
  });

  it('falls back to "generated" when neither cli nor model is known', () => {
    const view = generatedFileProvenance({
      file: 'status.md',
      kind: 'status',
      tokensIn: 0,
      tokensOut: 0,
      tokensTotal: 0,
      durationMs: 0,
    });

    expect(view?.producer).toBe('generated');
    expect(view?.model).toBeNull();
    expect(view?.cli).toBeNull();
  });

  it('returns null when no provenance exists', () => {
    expect(generatedFileProvenance(null)).toBeNull();
  });

  it('AGT-3045: adds the priced cost to the tooltip', () => {
    const view = generatedFileProvenance({
      file: 'status.md', kind: 'status', model: 'claude-sonnet-5', cli: 'claude',
      tokensIn: 1_000_000, tokensOut: 100_000, cacheReadTokens: 1_000_000, cacheCreationTokens: 0,
      tokensTotal: 2_100_000, durationMs: 0,
    }, {
      inputPerMillion: 3, outputPerMillion: 15, cacheReadPerMillion: 0.3, cacheWritePerMillion: 3.75,
      currency: 'USD', validFrom: '2026-01-01', source: null, note: null, unconfirmed: false,
    });
    expect(view?.tooltip).toContain('Cost: $4.80');
  });

  it('AGT-3045: says "price unknown" for an unpriced model', () => {
    const view = generatedFileProvenance({
      file: 'status.md', kind: 'status', model: 'mystery-model', cli: 'claude',
      tokensIn: 10, tokensOut: 5, tokensTotal: 15, durationMs: 0,
    }, null);
    expect(view?.tooltip).toContain('Cost: price unknown');
  });
});

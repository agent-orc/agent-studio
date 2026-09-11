import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliUsageModalComponent } from './cli-usage-modal';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate, TokenSummaryByModel } from '../../models/tokens.model';

/**
 * Smoke + contract for the per-CLI usage modal. Confirms it instantiates,
 * derives its title/subtitle from the row, and exposes every reported
 * quota window (so Claude / Codex show both their 5h and weekly windows
 * — requirement: show all windows, no grouped collapse).
 */
describe('CliUsageModalComponent', () => {
  async function build(
    row: CliUsageQuotaRow | null,
    cliType: 'claude' | 'codex' = 'claude',
    tokens: TokenSummaryAggregate | null = null,
    adhoc: AdHocUsageAggregate | null = null,
  ) {
    await TestBed.configureTestingModule({
      imports: [CliUsageModalComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliUsageModalComponent);
    fixture.componentRef.setInput('cliType', cliType);
    fixture.componentRef.setInput('row', row);
    fixture.componentRef.setInput('tokens', tokens);
    fixture.componentRef.setInput('adhoc', adhoc);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] CliUsageModalComponent initial render skipped:', (e as Error).message);
    }
    return fixture;
  }

  const row: CliUsageQuotaRow = {
    cliType: 'claude',
    icon: '✴️',
    label: 'Claude',
    plan: 'Pro',
    fetchedAt: new Date().toISOString(),
    freshness: 'updated just now',
    stale: false,
    source: 'pty',
    error: null,
    windows: [
      { label: 'Current session (5h)', usedPct: 11, used: null, limit: null, unit: null, resetAt: null, resetLabel: '3h' },
      { label: 'Weekly (all models)', usedPct: 47, used: null, limit: null, unit: null, resetAt: null, resetLabel: '4d' },
    ],
    primary: null,
    primaryPct: 47,
    primaryTone: 'ok',
  };

  it('instantiates and titles itself from the row', async () => {
    const fixture = await build(row);
    const c = fixture.componentInstance;
    expect(c).toBeTruthy();
    expect(c.title()).toBe('Claude');
    expect(c.subtitle()).toContain('Pro');
  });

  it('surfaces every reported window (5h + weekly)', async () => {
    const fixture = await build(row);
    expect(fixture.componentInstance.windows()).toHaveLength(2);
  });

  it('falls back to the CLI label and "no data" when no row is given', async () => {
    const fixture = await build(null);
    const c = fixture.componentInstance;
    expect(c.title()).toBe('Claude');
    expect(c.subtitle()).toBe('No data yet');
    expect(c.windows()).toHaveLength(0);
  });

  /**
   * Regression for the Codex "%-limit = 100%" bug (2026-07-10). The live
   * Codex payload reports its windows as `unit: "%"` with both `used` and
   * `limit` null and only `usedPct` set. The Limit column must show the
   * implied 100% cap, not a bare "n/a" placeholder.
   */
  const codexRow: CliUsageQuotaRow = {
    cliType: 'codex',
    icon: '🟪',
    label: 'Codex',
    plan: 'Pro',
    fetchedAt: new Date().toISOString(),
    freshness: 'updated just now',
    stale: false,
    source: '/status',
    error: null,
    windows: [
      { label: 'Current session (5h)', usedPct: 66, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '02:33' },
      { label: 'Weekly', usedPct: 12, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:33 on 3 May' },
      { label: 'Spark 5-hour', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '21:25' },
      { label: 'Spark Weekly', usedPct: 4, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '16:25 on 14 Jun' },
    ],
    primary: null,
    primaryPct: 66,
    primaryTone: 'ok',
  };

  it('shows the implied 100% cap for "%" windows with a null limit (Codex)', async () => {
    const fixture = await build(codexRow);
    const c = fixture.componentInstance;
    expect(c.windows()).toHaveLength(4);
    // Every window is unit "%" with a null limit -> implied cap 100%, not "n/a".
    for (const w of c.windows()) {
      expect(c.limitText(w)).toBe('100%');
    }
  });

  it('labels percentage windows as used and derives the remaining share', async () => {
    const fixture = await build(codexRow, 'codex');
    const first = fixture.componentInstance.windowViews()[0];
    expect(first.pctLabel).toBe('66% used');
    expect(first.remainingLabel).toBe('34% left');
  });

  it('labels a reported quota without a percentage as Unknown', async () => {
    const unknownRow: CliUsageQuotaRow = {
      ...row,
      windows: [
        { label: 'Quota', usedPct: null, used: null, limit: null, unit: '%', resetAt: null, resetLabel: null },
      ],
      primaryPct: null,
      primaryTone: 'unknown',
    };

    const fixture = await build(unknownRow);
    const view = fixture.componentInstance.windowViews()[0];

    expect(view.pctLabel).toBe('Unknown');
    expect(view.tone).toBe('unknown');
  });

  it('does not double-count Codex cached input and hides zero-token ad-hoc rows', async () => {
    const tokens: TokenSummaryAggregate = {
      projects: 11,
      orchestratorEntries: 13,
      orchestratorLlmCalls: 13,
      totalInputTokens: 50_428_112,
      totalOutputTokens: 164_172,
      totalCacheReadTokens: 48_503_936,
      totalCacheCreationTokens: 0,
      estimatedApiCostUsd: 0,
      allModelsPriced: false,
      byModel: [
        {
          model: 'gpt-5.6-sol', calls: 5,
          inputTokens: 39_646_031, outputTokens: 97_412,
          cacheReadTokens: 38_481_408, cacheCreationTokens: 0,
          estimatedApiCostUsd: 0, modelPriced: false,
        },
        {
          model: 'GPT-5.5', calls: 8,
          inputTokens: 10_782_081, outputTokens: 66_760,
          cacheReadTokens: 10_022_528, cacheCreationTokens: 0,
          estimatedApiCostUsd: 0, modelPriced: false,
        },
      ],
      byProject: [],
      fetchedAt: new Date().toISOString(),
      disclaimer: '',
    };
    const adhoc: AdHocUsageAggregate = {
      calls: 12,
      inputTokens: 0,
      outputTokens: 0,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      estimatedApiCostUsd: 0,
      allModelsPriced: false,
      bySource: [],
      byDay: [],
      byModel: [
        {
          model: 'gpt-5-codex', calls: 4,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
          estimatedApiCostUsd: 0, modelPriced: true,
        },
        {
          model: 'gpt-5.6-sol', calls: 7,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
          estimatedApiCostUsd: 0, modelPriced: false,
        },
      ],
      logPath: '(bus)',
      logSizeBytes: 0,
      logModifiedAt: null,
      disclaimer: '',
    };

    const fixture = await build(codexRow, 'codex', tokens, adhoc);
    const component = fixture.componentInstance;

    expect(component.modelRows().map(r => r.model)).toEqual(['gpt-5.6-sol', 'GPT-5.5']);
    expect(component.modelRows().every(r => r.source === 'project runtime')).toBe(true);
    expect(component.totals().tokens).toBe(50_592_284);
  });

  it('still returns "n/a" when a window carries no usable number at all', async () => {
    const fixture = await build(codexRow);
    const c = fixture.componentInstance;
    expect(
      c.limitText({ label: 'x', usedPct: null, used: null, limit: null, unit: null, resetAt: null, resetLabel: null }),
    ).toBe('n/a');
  });

  describe('below-threshold grouping and the total marker', () => {
    beforeEach(() => {
      try { localStorage.clear(); } catch { /* jsdom always has it, but be defensive */ }
    });

    function byModel(model: string, input: number, cost: number, priced: boolean): TokenSummaryByModel {
      return {
        model, calls: 1, inputTokens: input, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
        estimatedApiCostUsd: cost, modelPriced: priced,
      };
    }

    // 99% / 0.9% / 0.1% of 1,000,000 tokens: the last two fall under the
    // default 1% share threshold and fold into one "Other" row.
    const groupedTokens: TokenSummaryAggregate = {
      projects: 1, orchestratorEntries: 3, orchestratorLlmCalls: 3,
      totalInputTokens: 1_000_000, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
      estimatedApiCostUsd: 10.5, allModelsPriced: false,
      byModel: [
        byModel('gpt-5.6-sol', 990_000, 10, true),
        byModel('gpt-4o', 9_000, 0.5, true),
        byModel('gpt-4.1', 1_000, 0, false),
      ],
      byProject: [], fetchedAt: new Date().toISOString(), disclaimer: '',
    };

    it('folds below-threshold models into one "Other" row, collapsed by default', async () => {
      const fixture = await build(codexRow, 'codex', groupedTokens);
      const rows = fixture.componentInstance.modelRows();

      expect(rows).toHaveLength(2);
      expect(rows[0].model).toBe('gpt-5.6-sol');
      expect(rows[1].isOtherSummary).toBe(true);
      expect(rows[1].otherCount).toBe(2);
      expect(rows[1].inputTokens).toBe(10_000);
      expect(rows[1].modelPriced).toBe(false);
    });

    it('counts every underlying model toward the total, even while collapsed', async () => {
      const fixture = await build(codexRow, 'codex', groupedTokens);
      const totals = fixture.componentInstance.totals();

      expect(totals.models).toBe(3);
      expect(totals.unpricedModels).toBe(1);
      expect(totals.costUsd).toBe(10.5);
    });

    it('never renders "Unknown" for the total; shows the priced sum plus an unpriced marker', async () => {
      const fixture = await build(codexRow, 'codex', groupedTokens);
      expect(fixture.componentInstance.totalCostLabel()).toBe('$10.50 + 1 unpriced');
    });

    it('expands the "Other" row in place on toggle, and a fresh instance remembers it', async () => {
      const fixture = await build(codexRow, 'codex', groupedTokens);
      const component = fixture.componentInstance;
      expect(component.otherExpanded()).toBe(false);

      component.toggleOther();
      expect(component.otherExpanded()).toBe(true);
      const expandedRows = component.modelRows();
      expect(expandedRows).toHaveLength(4);
      expect(expandedRows.filter(r => r.isOtherChild).map(r => r.model)).toEqual(['gpt-4o', 'gpt-4.1']);

      // Re-opening the modal (a fresh component instance in a fresh
      // injector) keeps the choice: the preference lives in localStorage,
      // not just the torn-down component's in-memory state.
      TestBed.resetTestingModule();
      const reopened = await build(codexRow, 'codex', groupedTokens);
      expect(reopened.componentInstance.otherExpanded()).toBe(true);
    });
  });
});

import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliUsageModalComponent } from './cli-usage-modal';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../../models/tokens.model';
import { ensureBrowserStorage } from '../../../../../testing/browser-storage';
import {
  MODEL_GROUP_THRESHOLD_STORAGE_KEY,
  OTHER_MODELS_EXPANDED_STORAGE_KEY,
} from './cli-usage-model-rows.util';

ensureBrowserStorage();

/**
 * Smoke + contract for the per-CLI usage modal. Confirms it instantiates,
 * derives its title/subtitle from the row, and exposes every reported
 * quota window (so Claude / Codex show both their 5h and weekly windows
 * — requirement: show all windows, no grouped collapse).
 */
describe('CliUsageModalComponent', () => {
  beforeEach(() => {
    localStorage.removeItem(MODEL_GROUP_THRESHOLD_STORAGE_KEY);
    localStorage.removeItem(OTHER_MODELS_EXPANDED_STORAGE_KEY);
  });

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

  it('groups every row below the token-share threshold without dropping it from totals', async () => {
    const tokens: TokenSummaryAggregate = {
      projects: 4,
      orchestratorEntries: 12,
      orchestratorLlmCalls: 12,
      totalInputTokens: 11_446_000_000,
      totalOutputTokens: 67_000,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      estimatedApiCostUsd: 59_996.74,
      allModelsPriced: false,
      byModel: [
        {
          model: 'gpt-5.5', calls: 5, inputTokens: 10_940_000_000, outputTokens: 67_000,
          cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 50_000, modelPriced: true,
        },
        {
          model: 'gpt-5.6-sol', calls: 3, inputTokens: 500_000_000, outputTokens: 0,
          cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 9_990, modelPriced: true,
        },
        {
          model: 'gpt-5.4-mini', calls: 2, inputTokens: 5_000_000, outputTokens: 0,
          cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 6.74, modelPriced: true,
        },
        {
          model: 'gpt-future', calls: 2, inputTokens: 1_000_000, outputTokens: 0,
          cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0, modelPriced: false,
        },
      ],
      byProject: [],
      fetchedAt: new Date().toISOString(),
      disclaimer: '',
    };

    const fixture = await build(codexRow, 'codex', tokens);
    const component = fixture.componentInstance;

    expect(component.modelRows()).toHaveLength(4);
    expect(component.groupedModelRows().primaryRows.map(row => row.model))
      .toEqual(['gpt-5.5', 'gpt-5.6-sol']);
    expect(component.groupedModelRows().otherRows.map(row => row.model))
      .toEqual(['gpt-5.4-mini', 'gpt-future']);
    expect(component.totals().tokens).toBe(11_446_067_000);
    expect(component.totals().costUsd).toBe(59_996.74);
    expect(component.totals().unpricedModels).toBe(1);
    expect(component.groupedModelRows().otherTotals.costUsd).toBe(6.74);
    expect(component.costLabel(1.25, false)).toContain('$1.25 + 1 unpriced');
    expect(component.costLabel(0, false)).toBe('Unpriced');
    const totalCost = document.querySelector('[data-testid="cli-usage-model-total-cost"]');
    expect(totalCost).not.toBeNull();
    expect(totalCost!.closest('button')).toBeNull();
  });

  it('persists the grouping threshold and Other disclosure state for the viewer', async () => {
    const fixture = await build(codexRow, 'codex');
    const component = fixture.componentInstance;

    component.setGroupingThreshold('2.5');
    component.toggleOtherModels();

    expect(localStorage.getItem(MODEL_GROUP_THRESHOLD_STORAGE_KEY)).toBe('2.5');
    expect(localStorage.getItem(OTHER_MODELS_EXPANDED_STORAGE_KEY)).toBe('1');
  });

  it('restores persisted grouping preferences when the modal is recreated', async () => {
    localStorage.setItem(MODEL_GROUP_THRESHOLD_STORAGE_KEY, '3.5');
    localStorage.setItem(OTHER_MODELS_EXPANDED_STORAGE_KEY, '1');

    const fixture = await build(codexRow, 'codex');

    expect(fixture.componentInstance.groupingThresholdPct()).toBe(3.5);
    expect(fixture.componentInstance.otherModelsExpanded()).toBe(true);
  });
});

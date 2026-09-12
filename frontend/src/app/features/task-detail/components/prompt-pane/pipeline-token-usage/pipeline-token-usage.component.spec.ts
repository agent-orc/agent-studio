import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PipelineTokenUsageComponent } from './pipeline-token-usage.component';
import type {
  PipelineModelTokenUsage,
  PipelineModelUsageSummary,
  PipelineRunTokenUsage,
} from '../../../../task-pipeline';

function model(
  name: string,
  total: number,
  cost: number,
  known = true,
  steps = 1,
): PipelineModelTokenUsage {
  return {
    model: name,
    modelKnown: known,
    steps,
    inputTokens: total,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens: total,
    costUsd: cost,
  };
}

function run(attempt: number, current: boolean, models: PipelineModelTokenUsage[]): PipelineRunTokenUsage {
  return {
    attempt,
    current,
    startedAt: '2026-06-09T10:00:00Z',
    completedAt: '2026-06-09T10:05:00Z',
    models,
    totalTokens: models.reduce((a, m) => a + m.totalTokens, 0),
    totalCostUsd: models.reduce((a, m) => a + m.costUsd, 0),
    anyModelUnknown: models.some((m) => m.totalTokens > 0 && !m.modelKnown),
    tokenUsageAvailable: true,
  };
}

const SUMMARY: PipelineModelUsageSummary = {
  runs: [
    run(1, false, [model('claude-haiku-4-5', 1_200_000, 2)]),
    run(2, true, [
      model('claude-haiku-4-5', 1_200_000, 2),
      model('claude-opus-4-8', 110_000, 0.75),
    ]),
  ],
  totalByModel: [
    model('claude-haiku-4-5', 2_400_000, 4, true, 3),
    model('claude-opus-4-8', 110_000, 0.75),
  ],
  totalTokens: 2_510_000,
  totalCostUsd: 4.75,
  anyModelUnknown: false,
};

function setup(summary: PipelineModelUsageSummary | null) {
  TestBed.configureTestingModule({
    imports: [PipelineTokenUsageComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(PipelineTokenUsageComponent);
  fixture.componentRef.setInput('summary', summary);
  fixture.detectChanges();
  return fixture;
}

const root = (fixture: { nativeElement: HTMLElement }) => fixture.nativeElement as HTMLElement;
const all = (el: HTMLElement, sel: string) => el.querySelectorAll(`[data-testid="${sel}"]`);
const one = (el: HTMLElement, sel: string) => el.querySelector(`[data-testid="${sel}"]`);

describe('PipelineTokenUsageComponent', () => {
  it('renders nothing when the summary is null', () => {
    const fixture = setup(null);
    expect(one(root(fixture), 'pipeline-token-usage')).toBeNull();
  });

  it('renders nothing when there are no runs', () => {
    const fixture = setup({ runs: [], totalByModel: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false });
    expect(one(root(fixture), 'pipeline-token-usage')).toBeNull();
  });

  it('TASK TOTAL SUM is collapsed by default: lifetime total shows, model split hidden', () => {
    const fixture = setup(SUMMARY);
    // The total toggle line is always visible with the lifetime tokens + cost.
    expect(one(root(fixture), 'pipeline-token-usage-total')).not.toBeNull();
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-tokens')?.textContent).toContain('2.51M');
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent).toContain('$4.75');
    // Collapsed: the all-runs-by-model breakdown is not in the DOM yet.
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(0);
  });

  it('expanding TASK TOTAL SUM reveals the all-runs-by-model breakdown inline', () => {
    const fixture = setup(SUMMARY);
    fixture.componentInstance.toggleSummary();
    fixture.detectChanges();
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(2);
  });

  it('renders one collapsible row per run, every run collapsed by default', () => {
    const fixture = setup(SUMMARY);
    const runs = all(root(fixture), 'pipeline-token-usage-run');
    expect(runs.length).toBe(2);
    // No per-model rows until a run is expanded.
    expect(all(root(fixture), 'pipeline-token-usage-run-model').length).toBe(0);

    // Newest-first: the current run (#2) renders on top and carries the badge.
    const first = runs[0];
    expect(first.getAttribute('data-current')).toBe('true');
    expect(first.querySelector('[data-testid="pipeline-token-usage-run-current"]')).not.toBeNull();
  });

  it('maps each run onto the recorded task-token share and run duration', () => {
    const fixture = setup(SUMMARY);
    const shares = all(root(fixture), 'pipeline-token-usage-run-share');

    expect(shares[0].getAttribute('aria-label')).toBe(
      'Run #2: 52.2% of recorded task tokens',
    );
    expect(Number.parseFloat((shares[0].firstElementChild as HTMLElement).style.width))
      .toBeCloseTo(52.19, 2);
    expect(fixture.componentInstance.durationLabel(SUMMARY.runs[1])).toBe('5m');
  });

  it('expanding a run reveals only that run\'s per-model rows', () => {
    const fixture = setup(SUMMARY);
    fixture.componentInstance.toggleRun(2);
    fixture.detectChanges();

    const currentRun = root(fixture).querySelector(
      '[data-testid="pipeline-token-usage-run"][data-current="true"]',
    ) as HTMLElement;
    expect(currentRun.querySelectorAll('[data-testid="pipeline-token-usage-run-model"]').length).toBe(2);
    // The other (collapsed) run still shows no model rows.
    expect(all(root(fixture), 'pipeline-token-usage-run-model').length).toBe(2);
  });

  it('toggling the total button from the DOM expands and collapses the breakdown', () => {
    const fixture = setup(SUMMARY);
    const btn = one(root(fixture), 'pipeline-token-usage-total-toggle') as HTMLButtonElement;
    btn.click();
    fixture.detectChanges();
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(2);
    btn.click();
    fixture.detectChanges();
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(0);
  });

  it('renders an explicit no-price state for a model with no price on file', () => {
    const summary: PipelineModelUsageSummary = {
      runs: [{
        ...run(1, true, [model('gpt-5.6-sol', 700, 0, false)]),
        pricingGaps: [{ modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 }],
      }],
      totalByModel: [{
        ...model('gpt-5.6-sol', 700, 0, false),
        unpricedRuns: 1,
        pricingGaps: [{ modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 }],
      }],
      totalTokens: 700,
      totalCostUsd: 0,
      anyModelUnknown: true,
      unpricedRuns: 1,
      pricingGaps: [{ modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 }],
    };
    const fixture = setup(summary);
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .toContain('no price data');
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .not.toContain('$0.00');
    expect(fixture.componentInstance.totalTooltip(
      summary.totalTokens,
      summary.totalCostUsd,
      summary.anyModelUnknown,
      'Task total',
      summary.unpricedRuns,
      summary.pricingGaps,
    )).toContain('gpt-5.6-sol');

    fixture.componentInstance.toggleSummary();
    fixture.detectChanges();
    const modelRow = one(root(fixture), 'pipeline-token-usage-total-model');
    expect(modelRow?.textContent).toContain('no price data');
  });

  it('shows the priced subtotal plus an explicit incomplete-run marker for mixed runs', () => {
    const gap = { modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 };
    const summary: PipelineModelUsageSummary = {
      runs: [
        run(1, false, [model('claude-haiku-4-5', 1_200_000, 2)]),
        { ...run(2, true, [model('gpt-5.6-sol', 700, 0, false)]), pricingGaps: [gap] },
      ],
      totalByModel: [
        model('claude-haiku-4-5', 1_200_000, 2),
        { ...model('gpt-5.6-sol', 700, 0, false), unpricedRuns: 1, pricingGaps: [gap] },
      ],
      totalTokens: 1_200_700,
      totalCostUsd: 2,
      anyModelUnknown: true,
      unpricedRuns: 1,
      pricingGaps: [gap],
    };

    const fixture = setup(summary);
    const total = one(root(fixture), 'pipeline-token-usage-grand-total-cost');
    expect(total?.textContent).toContain('$2.00');
    expect(total?.textContent).toContain('incomplete (1 run without price)');
  });

  it('shows a recorded partial sum and names runs with missing token telemetry', () => {
    const recorded = model('gpt-5.6-sol', 600_000, 5.5);
    const summary: PipelineModelUsageSummary = {
      runs: [
        run(1, false, [recorded]),
        { ...run(2, true, []), tokenUsageAvailable: false },
      ],
      totalByModel: [recorded],
      totalTokens: 600_000,
      totalCostUsd: 5.5,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
      missingTokenRuns: 1,
    };

    const fixture = setup(summary);
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-tokens')?.textContent)
      .toContain('600.0k');
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .toContain('$5.50');
    expect(one(root(fixture), 'pipeline-token-usage-missing-runs')?.textContent)
      .toContain('incomplete (1 run without usage)');
    expect(all(root(fixture), 'pipeline-token-usage-run-cost')[0]?.textContent)
      .toContain('no usage data');
  });

  it('never renders silent zero totals when every visible run lacks telemetry', () => {
    const missingRuns = Array.from({ length: 6 }, (_, index) => ({
      ...run(index + 1, index === 5, []),
      tokenUsageAvailable: false,
    }));
    const summary: PipelineModelUsageSummary = {
      runs: missingRuns,
      totalByModel: [],
      totalTokens: 0,
      totalCostUsd: 0,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
      missingTokenRuns: 6,
    };

    const fixture = setup(summary);
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-tokens')?.textContent?.trim())
      .toBe('-');
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .toContain('no usage data');
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .not.toContain('$0.00');
    expect(one(root(fixture), 'pipeline-token-usage-missing-runs')?.textContent)
      .toContain('incomplete (6 runs without usage)');

    fixture.componentInstance.toggleSummary();
    fixture.detectChanges();
    expect(one(root(fixture), 'pipeline-token-usage-empty')?.textContent)
      .toContain('Token usage was not recorded');
  });

  it('keeps a real zero-dollar value when the run consumed no tokens', () => {
    const summary: PipelineModelUsageSummary = {
      runs: [run(1, true, [])],
      totalByModel: [],
      totalTokens: 0,
      totalCostUsd: 0,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
    };

    const fixture = setup(summary);
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .toContain('$0.00');
  });

  it('shows the resolved gpt-5.6 amount without an incomplete marker after catalog rollout', () => {
    const priced = model('gpt-5.6-sol', 600_000, 5.5);
    const summary: PipelineModelUsageSummary = {
      runs: [run(1, true, [priced])],
      totalByModel: [priced],
      totalTokens: 600_000,
      totalCostUsd: 5.5,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
    };

    const fixture = setup(summary);
    const total = one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent ?? '';
    expect(total).toContain('$5.50');
    expect(total).not.toContain('incomplete');
    expect(total).not.toContain('no price data');
  });

  it('marks a mixed priced and unpriced lifetime aggregate as partial', () => {
    const summary: PipelineModelUsageSummary = {
      runs: [run(1, true, [
        model('claude-haiku-4-5', 700, 0.25),
        model('unpriced-test-model', 300, 0, false),
      ])],
      totalByModel: [
        model('claude-haiku-4-5', 700, 0.25),
        model('unpriced-test-model', 300, 0, false),
      ],
      totalTokens: 1_000,
      totalCostUsd: 0.25,
      anyModelUnknown: true,
    };

    const fixture = setup(summary);
    const total = one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent ?? '';
    expect(total).toContain('$0.25');
    expect(total).toContain('incomplete (1 run without price)');
  });
});

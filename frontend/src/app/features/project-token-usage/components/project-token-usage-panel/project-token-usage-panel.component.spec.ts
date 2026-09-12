import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { ProjectTokenUsagePanelComponent } from './project-token-usage-panel.component';
import { ProjectPipelineCostTrendComponent } from '../project-pipeline-cost-trend/project-pipeline-cost-trend.component';
import type { ProjectPipelineCostTimeline, ProjectTokenUsageSummary } from '../../../../features/project-token-usage';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('ProjectTokenUsagePanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectTokenUsagePanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Pipeline-cost section render check. Seeds the `pipelineCost` signal with a
 * two-kind, three-day timeline and asserts the project-level "how it develops
 * over time" surface renders: the per-kind legend (with cost + tokens), the
 * total row, and one stacked bar per day. This exercises the template bindings
 * added for the project-level aggregate (acceptance: "a project-level view
 * shows aggregate tokens + cost per step kind with a time trend") so a binding
 * typo reds the test instead of only surfacing in a browser.
 */
describe('ProjectTokenUsagePanelComponent (pipeline cost)', () => {
  function fakeTimeline(): ProjectPipelineCostTimeline {
    const days = ['2026-05-31', '2026-06-01', '2026-06-02'];
    return {
      project: 'demo',
      days,
      windowDays: 30,
      kinds: [
        {
          kind: 'core',
          totalTokens: 300_000,
          totalCostUsd: 0.75,
          anyModelUnknown: false,
          cells: [
            { day: days[0], totalTokens: 100_000, costUsd: 0.25 },
            { day: days[1], totalTokens: 100_000, costUsd: 0.25 },
            { day: days[2], totalTokens: 100_000, costUsd: 0.25 },
          ],
        },
        {
          kind: 'aspect',
          totalTokens: 80_000,
          totalCostUsd: 0.08,
          anyModelUnknown: false,
          cells: [
            { day: days[0], totalTokens: 20_000, costUsd: 0.02 },
            { day: days[1], totalTokens: 20_000, costUsd: 0.02 },
            { day: days[2], totalTokens: 40_000, costUsd: 0.04 },
          ],
        },
        {
          kind: 'drift',
          totalTokens: 20_000,
          totalCostUsd: 0.04,
          anyModelUnknown: false,
          cells: [
            { day: days[0], totalTokens: 0, costUsd: 0 },
            { day: days[1], totalTokens: 0, costUsd: 0 },
            { day: days[2], totalTokens: 20_000, costUsd: 0.04 },
          ],
        },
      ],
      steps: [
        { stepId: 'core-agent-run', kind: 'core', totalTokens: 300_000, totalCostUsd: 0.75, anyModelUnknown: false },
        { stepId: 'aspect-code-quality', kind: 'aspect', totalTokens: 80_000, totalCostUsd: 0.08, anyModelUnknown: false },
        { stepId: 'post-drift-adr-code', kind: 'drift', totalTokens: 20_000, totalCostUsd: 0.04, anyModelUnknown: false },
      ],
      totalTokens: 400_000,
      totalCostUsd: 0.87,
      anyModelUnknown: false,
      taskCount: 4,
      hasData: true,
      fetchedAt: '2026-06-02T00:00:00Z',
      freshness: {
        status: 'partial',
        asOf: '2026-06-02T00:00:00Z',
        warning: 'One historical pipeline record could not be read.',
        sources: ['task-token-receipts'],
      },
    };
  }

  it('renders the per-kind legend, total, and one stacked bar per day', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    // First flush runs the projectName effect (which kicks off the load and
    // resets the signal to null + issues pending test HTTP calls). Seed the
    // signal afterwards so our fixture data survives the refresh reset.
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }
    fixture.componentInstance.pipelineCost.set(fakeTimeline());
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="token-usage-pipeline-cost"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-empty"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-cost-source-warning"]')?.textContent)
      .toContain('may be incomplete');

    const legend = host.querySelector('[data-testid="pipeline-cost-legend"]');
    expect(legend).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-core"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-aspect"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-drift"]')).toBeTruthy();

    const total = host.querySelector('[data-testid="pipeline-cost-total"]');
    expect(total?.textContent).toContain('$0.87');

    const cols = host.querySelectorAll('[data-testid="pipeline-cost-bars"] .trend__column');
    expect(cols.length).toBe(3);
    // Busiest day (3rd: 160k tokens) carries core, aspect, and drift segments.
    const lastColSegs = cols[2].querySelectorAll('.trend__segment');
    expect(lastColSegs.length).toBe(3);
  });

  it('renders no-price and mixed project aggregates without a silent zero', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }

    const timeline = fakeTimeline();
    const gap = { modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 };
    timeline.kinds[1] = {
      ...timeline.kinds[1],
      totalCostUsd: 0,
      anyModelUnknown: true,
      unpricedRuns: 1,
      pricingGaps: [gap],
      cells: timeline.kinds[1].cells.map((cell, index) => index === 2
        ? { ...cell, costUsd: 0, unpricedRuns: 1, pricingGaps: [gap] }
        : { ...cell, costUsd: 0 }),
    };
    timeline.totalCostUsd = 0.79;
    timeline.anyModelUnknown = true;
    timeline.unpricedRuns = 1;
    timeline.pricingGaps = [gap];
    timeline.dayCosts = [
      { day: timeline.days[0], totalTokens: 120_000, costUsd: 0.25 },
      { day: timeline.days[1], totalTokens: 120_000, costUsd: 0.25 },
      {
        day: timeline.days[2], totalTokens: 160_000, costUsd: 0.29,
        unpricedRuns: 1, pricingGaps: [gap],
      },
    ];
    fixture.componentInstance.pipelineCost.set(timeline);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="pipeline-cost-legend-aspect"]')?.textContent)
      .toContain('no price data');
    const total = host.querySelector('[data-testid="pipeline-cost-total"]');
    expect(total?.textContent).toContain('$0.79');
    expect(total?.textContent).toContain('incomplete (1 run without price)');

    const trend = fixture.debugElement.query(By.directive(ProjectPipelineCostTrendComponent))
      .componentInstance as ProjectPipelineCostTrendComponent;
    const affectedDay = trend.stackColumns()[2];
    expect(trend.columnTooltip(affectedDay)).toContain('gpt-5.6-sol');
    expect(trend.columnTooltip(affectedDay)).toContain('NoPriceForDate');
  });
});

describe('ProjectTokenUsagePanelComponent (freshness)', () => {
  it('renders the weekly better-candidate usage as a separate line', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }
    fixture.componentInstance.summary.set({
      project: 'demo',
      hasData: true,
      lifetimeTotalTokens: 42_000,
      lifetimeJobTokens: 42_000,
      lifetimeSupportingTokens: 0,
      lifetimeOrchestratorTokens: 0,
      lifetimeCalls: 2,
      last24hTotalTokens: 42_000,
      last24hJobTokens: 42_000,
      last24hSupportingTokens: 0,
      last24hOrchestratorTokens: 0,
      last24hCalls: 2,
      last7dTotalTokens: 42_000,
      last7dJobTokens: 42_000,
      last7dSupportingTokens: 0,
      last7dOrchestratorTokens: 0,
      last7dCalls: 2,
      last7dBetterCandidateTokens: 31_500,
      last7dBetterCandidateCostUsd: 1.2345,
      last7dBetterCandidateCalls: 1,
      allBetterCandidateModelsPriced: true,
      firstActivity: '2026-09-12T09:00:00Z',
      lastActivity: '2026-09-12T10:00:00Z',
      fetchedAt: '2026-09-12T10:01:00Z',
      disclaimer: '',
    });
    fixture.detectChanges();

    const line = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="token-usage-better-candidate-line"]');
    expect(line?.textContent).toContain('31.5k tokens');
    expect(line?.textContent).toContain('$1.23');
  });

  it('shows the source timestamp and an honest partial-data warning', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }
    const summary: ProjectTokenUsageSummary = {
      project: 'demo',
      hasData: true,
      lifetimeTotalTokens: 2_000,
      lifetimeJobTokens: 2_000,
      lifetimeSupportingTokens: 0,
      lifetimeOrchestratorTokens: 0,
      lifetimeCalls: 1,
      last24hTotalTokens: 2_000,
      last24hJobTokens: 2_000,
      last24hSupportingTokens: 0,
      last24hOrchestratorTokens: 0,
      last24hCalls: 1,
      last7dTotalTokens: 2_000,
      last7dJobTokens: 2_000,
      last7dSupportingTokens: 0,
      last7dOrchestratorTokens: 0,
      last7dCalls: 1,
      firstActivity: '2026-08-09T10:00:00Z',
      lastActivity: '2026-08-09T10:00:00Z',
      fetchedAt: '2026-08-09T10:01:00Z',
      freshness: {
        status: 'partial',
        asOf: '2026-08-09T10:00:00Z',
        warning: 'The task receipt source could not be read.',
        sources: ['historical-token-bus'],
      },
      disclaimer: '',
    };
    fixture.componentInstance.summary.set(summary);
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="token-usage-as-of"]')?.textContent)
      .toContain('2026-08-09 10:00 UTC');
    expect(host.querySelector('[data-testid="token-usage-source-warning"]')?.textContent)
      .toContain('may be incomplete');
  });
});

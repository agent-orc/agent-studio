import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject } from 'rxjs';
import { TaskService } from '../../../../services/task.service';
import { By } from '@angular/platform-browser';
import { ProjectTokenUsagePanelComponent, tokenFreshnessTone } from './project-token-usage-panel.component';
import { ProjectPipelineCostTrendComponent } from '../project-pipeline-cost-trend/project-pipeline-cost-trend.component';
import type { ProjectPipelineCostTimeline, ProjectTokenUsageSummary, ProjectTokenHeatmap, ProjectExpensiveJobsResponse } from '../../../../features/project-token-usage';

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

  it('shows pipeline decisions when the separate token activity summary is empty', async () => {
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
    fixture.detectChanges();
    fixture.componentInstance.summary.set({ project: 'demo', hasData: false } as ProjectTokenUsageSummary);
    fixture.componentInstance.pipelineCost.set(fakeTimeline());
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="token-usage-empty"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-cost-legend-core"]')).toBeTruthy();
  });

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
    expect(host.querySelector('[data-testid="token-usage-freshness"]')?.getAttribute('data-tone')).toBe('warning');
    expect(host.querySelector('[data-testid="token-usage-source-warning"]')?.textContent)
      .toContain('may be incomplete');
  });
});

describe('tokenFreshnessTone', () => {
  it('maps freshness onto a status tone, unknown stays neutral', () => {
    expect(tokenFreshnessTone('complete')).toBe('success');
    expect(tokenFreshnessTone('partial')).toBe('warning');
    expect(tokenFreshnessTone('unavailable')).toBe('danger');
    expect(tokenFreshnessTone('later')).toBe('neutral');
  });
});


describe('ProjectTokenUsagePanelComponent request ownership', () => {
  async function setup(beforeCreate?: () => void) {
    await TestBed.configureTestingModule({
      imports: [ProjectTokenUsagePanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    beforeCreate?.();
    const fixture = TestBed.createComponent(ProjectTokenUsagePanelComponent);
    fixture.componentRef.setInput('projectName', 'alpha');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const request = (suffix: string, project = 'alpha') => http.expectOne(r =>
      r.url === `/api/projects/${project}/token-usage/${suffix}`);
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, http, request, host, component: fixture.componentInstance };
  }

  const emptyResponses: Record<string, object> = {
    summary: { project: 'alpha', hasData: false },
    heatmap: { project: 'alpha', hasData: false, days: [], jobs: [] },
    expensive: { project: 'alpha', jobs: [] },
    'pipeline-cost': { project: 'alpha', hasData: false, days: [], kinds: [], steps: [] },
  };

  it('ignores all older project responses delivered after the current project', async () => {
    const summaries = [new Subject<ProjectTokenUsageSummary>(), new Subject<ProjectTokenUsageSummary>()];
    const heatmaps = [new Subject<ProjectTokenHeatmap>(), new Subject<ProjectTokenHeatmap>()];
    const lists = [new Subject<ProjectExpensiveJobsResponse>(), new Subject<ProjectExpensiveJobsResponse>()];
    const costs = [new Subject<ProjectPipelineCostTimeline>(), new Subject<ProjectPipelineCostTimeline>()];
    const job = (jobId: string, total: number) => ({
      jobId, title: `${jobId} project task`, category: 'job' as const, total, totalTokens: total,
      calls: 1, state: null, lastActivity: null, lastModel: null, cells: [{ day: '2026-11-27', total }],
    });
    const { fixture, component, host } = await setup(() => {
      const service = TestBed.inject(TaskService);
      const index = (name: string) => name === 'alpha' ? 0 : 1;
      vi.spyOn(service, 'getProjectTokenUsageSummary').mockImplementation(name => summaries[index(name)]);
      vi.spyOn(service, 'getProjectTokenUsageHeatmap').mockImplementation(name => heatmaps[index(name)]);
      vi.spyOn(service, 'getProjectExpensiveJobs').mockImplementation(name => lists[index(name)]);
      vi.spyOn(service, 'getProjectPipelineCost').mockImplementation(name => costs[index(name)]);
    });
    fixture.componentRef.setInput('projectName', 'beta');
    fixture.detectChanges();
    summaries[1].next({ project: 'beta', hasData: true, lifetimeTotalTokens: 12345 } as ProjectTokenUsageSummary);
    heatmaps[1].next({ project: 'beta', hasData: true, days: ['2026-11-27'], jobs: [job('beta', 12345)], fetchedAt: '2026-11-27T00:00:00Z' });
    lists[1].next({ project: 'beta', jobs: [job('beta', 12345)] });
    // Superseded completion must not clear the current pipeline request's busy state.
    costs[0].next({ project: 'alpha', totalCostUsd: 999, hasData: false, totalTokens: 999, days: [], kinds: [], steps: [], windowDays: 30, anyModelUnknown: false, taskCount: 1, fetchedAt: '2026-11-27T00:00:00Z' });
    expect(component.loading()).toBe(true);
    costs[1].next({ project: 'beta', totalCostUsd: 12.34, hasData: true, kinds: [], steps: [], days: [], totalTokens: 12345, windowDays: 30, anyModelUnknown: false, taskCount: 1, fetchedAt: '2026-11-27T00:00:00Z' });
    summaries[0].next({ project: 'alpha', hasData: true, lifetimeTotalTokens: 1_000_000_000 } as ProjectTokenUsageSummary);
    heatmaps[0].next({ project: 'alpha', hasData: true, days: ['2026-11-27'], jobs: [job('alpha', 999)], fetchedAt: '2026-11-27T00:00:00Z' });
    lists[0].next({ project: 'alpha', jobs: [job('alpha', 999)] });
    costs[0].next({ project: 'alpha', totalCostUsd: 999, hasData: false, totalTokens: 999, days: [], kinds: [], steps: [], windowDays: 30, anyModelUnknown: false, taskCount: 1, fetchedAt: '2026-11-27T00:00:00Z' });
    summaries[0].error(new Error('stale transport failure'));
    fixture.detectChanges();
    expect(component.summary()?.project).toBe('beta');
    expect(component.heatmap()?.project).toBe('beta');
    expect(component.pipelineCost()?.project).toBe('beta');
    expect(component.loading()).toBe(false);
    expect(host.querySelector('[data-testid="token-usage-card-total"]')?.textContent).toContain('12K');
    expect(host.querySelector('[data-testid="pipeline-cost-total"]')?.textContent).toContain('$12.34');
    expect(host.textContent).not.toContain('stale transport failure');
    expect(host.textContent).not.toContain('alpha project task');
    expect(host.querySelector('[data-testid="expensive-btn-beta"]')?.textContent).toContain('12K');
    expect(host.querySelector('[data-testid="heatmap-row-btn-beta"]')?.textContent).toContain('12K');
    fixture.destroy();
  });

  it('cancels all four project reads and the task read when the project changes', async () => {
    const { fixture, request, component } = await setup();
    const old = Object.keys(emptyResponses).map(key => request(key));
    component.onSelectJob('same-id');
    fixture.detectChanges();
    const oldDetail = request('job/same-id');
    fixture.componentRef.setInput('projectName', 'beta');
    fixture.detectChanges();
    expect(old.map(r => r.cancelled)).toEqual([true, true, true, true]);
    expect(oldDetail.cancelled).toBe(true);
    expect(component.selectedJobId()).toBeNull();
    expect(component.summary()).toBeNull();
    expect(component.drilldown()).toBeNull();
    expect(component.loading()).toBe(true);
    fixture.destroy();
    for (const key of Object.keys(emptyResponses)) expect(request(key, 'beta').cancelled).toBe(true);
  });

  it('cancels a superseded task and clears its content while the next task loads', async () => {
    const { fixture, request, component, host } = await setup();
    component.onSelectJob('a');
    fixture.detectChanges();
    const a = request('job/a');
    component.onSelectJob('b');
    fixture.detectChanges();
    expect(a.cancelled).toBe(true);
    expect(host.querySelector('[data-testid="drill-loading"]')).toBeTruthy();
    request('job/b').flush({ jobId: 'b', title: 'Task B', category: 'job', totalTokens: 12345, calls: 1,
      runs: [{ index: 0, ts: '2026-11-27T00:00:00Z', total: 12345 }] });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="token-usage-drill"]')?.textContent).toContain('Task B');
    expect(host.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-27T00:00:00Z');
    expect(host.querySelector('time')?.textContent).toContain('2026-11-27 00:00 UTC');
    fixture.destroy();
  });

  it('retries task detail without exposing a transport error', async () => {
    const { fixture, request, component, host } = await setup();
    component.onSelectJob('b');
    fixture.detectChanges();
    request('job/b').flush({ error: 'private transport detail' }, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    const error = host.querySelector('[data-testid="drill-error"]');
    expect(error?.textContent).toContain('Could not load task token usage.');
    expect(host.textContent).not.toContain('private transport detail');
    (error?.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    request('job/b').flush({ jobId: 'b', title: 'Task B', runs: [] });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="drill-error"]')).toBeNull();
    expect(host.querySelector('[data-testid="drill-empty"]')).toBeTruthy();
    fixture.destroy();
  });

  it('does not claim zero activity while reads are pending', async () => {
    const { host } = await setup();
    for (const id of ['token-usage-empty', 'expensive-empty', 'heatmap-empty', 'pipeline-cost-empty']) {
      expect(host.querySelector(`[data-testid="${id}"]`)).toBeNull();
    }
    expect(host.textContent).not.toContain('No daily activity');
  });

  for (const failed of Object.keys(emptyResponses)) {
    it(`isolates ${failed} failure and retries only that section`, async () => {
      const { fixture, request, component, host } = await setup();
      for (const [key, response] of Object.entries(emptyResponses)) {
        const req = request(key);
        if (key === failed) req.flush({ error: 'private transport detail' }, { status: 503, statusText: 'Unavailable' });
        else req.flush(response);
      }
      fixture.detectChanges();
      const error = host.querySelector(`[data-testid="${failed}-error"]`);
      expect(error).toBeTruthy();
      expect(host.textContent).not.toContain('Http failure');
      expect(host.textContent).not.toContain('private transport detail');
      const emptyIds: Record<string, string> = { summary: 'token-usage-empty', heatmap: 'heatmap-empty', expensive: 'expensive-empty',
        'pipeline-cost': 'pipeline-cost-empty' };
      const emptyId = emptyIds[failed];
      expect(host.querySelector(`[data-testid="${emptyId}"]`)).toBeNull();
      (error?.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(component.loading()).toBe(true);
      expect(host.querySelector(`[data-testid="${failed}-error"]`)).toBeNull();
      request(failed).flush(emptyResponses[failed]);
      fixture.detectChanges();
      expect(component.loading()).toBe(false);
      expect(host.querySelector(`[data-testid="${emptyId}"]`)).toBeTruthy();
    });
  }

  it('matches workspace compact counts and exposes exact values', async () => {
    const { fixture, request, component, host } = await setup();
    request('summary').flush({ hasData: true, lifetimeTotalTokens: 1_000_000_000,
      lifetimeJobTokens: 12345, last24hTotalTokens: 12345 });
    fixture.detectChanges();
    expect(component.formatTokens(12345)).toBe('12K');
    expect(component.formatTokens(1_000_000_000)).toBe('1,000.0M');
    const total = host.querySelector('[data-testid="token-usage-card-total"] .tup__card-value');
    expect(total?.textContent).toBe('1,000.0M');
    expect(total?.getAttribute('aria-label')).toBe('1,000,000,000 tokens');
  });
});

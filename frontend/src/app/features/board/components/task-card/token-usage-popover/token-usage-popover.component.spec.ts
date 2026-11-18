import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskTokenUsagePopoverComponent } from './token-usage-popover.component';
import { buildTokenBubble } from '../task-card-view-model';
import type { TaskInfo } from '../../../../../models/task.model';
import { browserTimeZone, formatZonedWithUtc } from '../../../../../services/usage-time.util';

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    title: 'Task 1',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-05-11T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-05-11T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  } as TaskInfo;
}

describe('TaskTokenUsagePopoverComponent', () => {
  async function render(job: TaskInfo) {
    await TestBed.configureTestingModule({
      imports: [TaskTokenUsagePopoverComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskTokenUsagePopoverComponent);
    fixture.componentRef.setInput('job', job);
    fixture.componentRef.setInput('bubble', buildTokenBubble(job.tokenSummary));
    fixture.detectChanges();
    return fixture;
  }

  it('shows the last update in the shared zoned usage format', async () => {
    const at = '2026-11-13T04:39:48Z';
    const job = makeJob({ tokenSummary: {
      calls: 1, inputTokens: 100, outputTokens: 0, cacheReadTokens: 0,
      cacheCreationTokens: 0, totalTokens: 100, lastModel: 'codex', lastUpdate: at, entries: [],
    } });
    const fixture = await render(job);
    const lastUpdate = fixture.nativeElement.querySelector('[data-testid="token-row-last-update"]');
    expect(lastUpdate?.textContent?.trim()).toBe(formatZonedWithUtc(at, browserTimeZone()));
    expect(lastUpdate?.textContent?.trim()).toMatch(/^Fri 13 Nov, \d{2}:39 .*UTC\)?$/);
  });

  it('renders each run priced at its own timestamp, not one combined estimate', async () => {
    const job = makeJob({
      tokenSummary: {
        calls: 2,
        inputTokens: 100,
        outputTokens: 50,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 150,
        estimatedApiCostUsd: 0.03,
        allModelsPriced: true,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-11T09:30:00Z',
        entries: [
          { ts: '2026-05-11T09:00:00Z', model: 'claude-opus-4-7', inputTokens: 60, outputTokens: 30, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.02, modelPriced: true },
          { ts: '2026-05-11T09:30:00Z', model: 'claude-opus-4-7', inputTokens: 40, outputTokens: 20, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.01, modelPriced: true },
        ],
      },
    });
    const fixture = await render(job);
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="token-usage-runs"] tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('$0.02');
    expect(rows[1].textContent).toContain('$0.01');

    TestBed.inject(HttpTestingController).verify();
  });

  it('shows the observed model and mismatch flag instead of the pinned model', async () => {
    const job = makeJob({
      tokenSummary: {
        calls: 2,
        inputTokens: 20,
        outputTokens: 4,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 24,
        estimatedApiCostUsd: 0.001,
        allModelsPriced: true,
        lastModel: 'Claude Haiku 4.5',
        lastUpdate: '2026-09-24T09:30:00Z',
        hasModelMismatch: true,
        entries: [
          { ts: '2026-09-24T09:00:00Z', model: 'claude-haiku-4-5', displayModel: 'Claude Haiku 4.5', pinnedModel: 'claude-opus-5-5', modelMismatch: true, inputTokens: 10, outputTokens: 2, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.0005, modelPriced: true },
          { ts: '2026-09-24T09:30:00Z', model: 'claude-haiku-4-5', displayModel: 'Claude Haiku 4.5', pinnedModel: 'claude-opus-5-5', modelMismatch: true, inputTokens: 10, outputTokens: 2, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0.0005, modelPriced: true },
        ],
      },
    });

    const fixture = await render(job);
    expect(fixture.nativeElement.querySelector('[data-testid="token-row-model"]').textContent)
      .toContain('Claude Haiku 4.5');
    expect(fixture.nativeElement.querySelector('[data-testid="token-model-mismatch"]')?.getAttribute('data-tone')).toBe('warning');
    expect(fixture.nativeElement.querySelector('[data-testid="token-run-model-mismatch"]').textContent)
      .toContain('pinned claude-opus-5-5');

    TestBed.inject(HttpTestingController).verify();
  });

  it('fetches the by-type breakdown only once ensureTypeBreakdownLoaded is called', async () => {
    const job = makeJob({
      tokenSummary: {
        calls: 1,
        inputTokens: 100,
        outputTokens: 50,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 150,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-11T09:30:00Z',
        entries: [],
      },
    });
    const fixture = await render(job);
    const httpMock = TestBed.inject(HttpTestingController);
    httpMock.expectNone((r) => r.url === '/api/tasks/task-1/pipeline');

    fixture.componentInstance.ensureTypeBreakdownLoaded();
    const req = httpMock.expectOne((r) => r.url === '/api/tasks/task-1/pipeline');
    req.flush({
      pipeline: { id: 'p', displayName: 'p', version: 1, pre: [], core: [], post: [] },
      execution: null,
      cost: {
        steps: [
          { stepId: 'core', kind: 'core', modelKnown: true, inputTokens: 100, outputTokens: 50, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 150, inputCostUsd: 0.02, outputCostUsd: 0.01, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0.03 },
        ],
        totalInputTokens: 100, totalOutputTokens: 50, totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 150,
        totalInputCostUsd: 0.02, totalOutputCostUsd: 0.01, totalCacheReadCostUsd: 0, totalCacheCreationCostUsd: 0, totalCostUsd: 0.03,
        anyModelUnknown: false,
      },
      config: {},
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.typeBreakdown()).toEqual([
      { kind: 'core', label: 'Core agent work', totalTokens: 150, costLabel: '$0.03' },
    ]);

    // A second call must not refetch — the component only loads once per instance.
    fixture.componentInstance.ensureTypeBreakdownLoaded();
    httpMock.expectNone((r) => r.url === '/api/tasks/task-1/pipeline');
  });

  it('reconciles the current attempt by-type rows separately from a multi-attempt lifetime total', async () => {
    const job = makeJob({
      tokenSummary: {
        calls: 2, inputTokens: 300, outputTokens: 150, cacheReadTokens: 0,
        cacheCreationTokens: 0, totalTokens: 450, estimatedApiCostUsd: 23.18,
        allModelsPriced: true, lastModel: 'claude-opus-4-7', lastUpdate: '2026-05-11T09:30:00Z',
        entries: [
          { ts: '2026-05-11T09:00:00Z', model: 'claude-opus-4-7', inputTokens: 200, outputTokens: 100, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 19.73, modelPriced: true },
          { ts: '2026-05-11T09:30:00Z', model: 'claude-opus-4-7', inputTokens: 100, outputTokens: 50, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 3.45, modelPriced: true },
        ],
      },
    });
    const fixture = await render(job);
    fixture.componentInstance.ensureTypeBreakdownLoaded();
    TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/tasks/task-1/pipeline').flush({
      pipeline: { id: 'p', displayName: 'p', version: 1, pre: [], core: [], post: [] },
      execution: null,
      cost: {
        steps: [
          { stepId: 'core', kind: 'core', modelKnown: true, inputTokens: 80, outputTokens: 40, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 120, inputCostUsd: 2, outputCostUsd: 0.83, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 2.83 },
          { stepId: 'aspect', kind: 'aspect', modelKnown: true, inputTokens: 20, outputTokens: 10, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 30, inputCostUsd: 0.4, outputCostUsd: 0.22, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd: 0.62 },
        ],
        totalInputTokens: 100, totalOutputTokens: 50, totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 150,
        totalInputCostUsd: 2.4, totalOutputCostUsd: 1.05, totalCacheReadCostUsd: 0, totalCacheCreationCostUsd: 0, totalCostUsd: 3.45,
        anyModelUnknown: false,
      },
      config: {},
    });
    fixture.detectChanges();

    const table = fixture.nativeElement.querySelector('[data-testid="token-usage-by-type"]') as HTMLTableElement;
    const rowCosts = Array.from(table.querySelectorAll('tbody tr td:last-child'), (cell) => Number(cell.textContent?.trim().replace('$', '')));
    const subtotal = table.querySelector('[data-testid="token-usage-by-type-total"]');
    expect(fixture.nativeElement.textContent).toContain('By type (current attempt)');
    expect(subtotal?.textContent).toContain('$3.45');
    expect(rowCosts.reduce((sum, cost) => sum + cost, 0)).toBeCloseTo(3.45);
    expect(fixture.nativeElement.querySelector('[data-testid="token-cost-tooltip"]').textContent).toContain('$23.18');
    TestBed.inject(HttpTestingController).verify();
  });
});

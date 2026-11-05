import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { PromptAdminPanelComponent } from './prompt-admin-panel.component';
import {
  PromptAdminService,
  PromptCatalogResponse,
  PromptCoverageResponse,
  PromptDetail,
  PromptPreviewResult,
  PromptReviewResult,
  PromptReviewRunResponse,
} from '../../../../services/prompt-admin.service';

const FRESH = 'runner-fresh-start.md';
const DRIFT = 'review-aspect-code-quality.md';

function calls(totalCalls = 0) {
  return {
    totalCalls,
    calls7d: totalCalls,
    lastCalledAt: totalCalls ? '2026-07-23T09:00:00Z' : null,
    inputTokens: totalCalls * 120,
    costUsd: totalCalls * 0.0001,
    costUsd7d: totalCalls * 0.0001,
    unpricedCalls: 0,
    unpricedCalls7d: 0,
    currentVersionCalls: totalCalls,
    isDead: totalCalls === 0,
    daily: [{ date: '2026-07-23', calls: totalCalls, inputTokens: totalCalls * 120, costUsd: totalCalls * 0.0001 }],
    versions: [],
  };
}

function catalog(): PromptCatalogResponse {
  return {
    overrideDirectory: '/tmp/prompt-overrides',
    orphanedOverrides: [],
    telemetryPath: '/tmp/prompt-calls.jsonl',
    deadPromptDays: 30,
    costDisclaimer: 'Theoretical API-equivalent estimate.',
    items: [
      {
        name: FRESH,
        title: 'Runner: fresh start',
        description: 'Bootstrap prompt handed to the CLI agent when a task starts from scratch.',
        group: 'Runner',
        promptClass: 'runtime-step',
        hasDefault: true,
        hasOverride: false,
        defaultChangedSinceOverride: false,
        slots: ['taskId', 'thing'],
        usageCount: 1,
        lastChangedAt: '2026-07-21T10:00:00Z',
        lastChangedSha: 'abc12345def',
        lastReviewedAt: '2026-07-22T10:00:00Z',
        reviewStatus: 'current',
        reviewFindingCount: 0,
        projectOverrideCount: 0,
        calls: calls(12),
      },
      {
        name: DRIFT,
        title: 'Aspect: code quality',
        description: 'Review aspect that grades code quality of the change.',
        group: 'Review',
        promptClass: 'runtime-step',
        hasDefault: true,
        hasOverride: true,
        defaultChangedSinceOverride: true,
        slots: ['diff'],
        usageCount: 1,
        lastChangedAt: '2026-07-20T10:00:00Z',
        lastChangedSha: 'def67890aaa',
        lastReviewedAt: null,
        reviewStatus: null,
        reviewFindingCount: 0,
        projectOverrideCount: 1,
        calls: calls(),
      },
    ],
  };
}

function coverage(): PromptCoverageResponse {
  return {
    totalSites: 6,
    coveredSites: 4,
    pendingSites: 2,
    items: [
      { component: 'backend/Features/Runner/ProjectRunner.cs', status: 'covered', detail: 'Migrated to templates.' },
      { component: 'backend/Features/Runner/ReviewDecisionOrchestrator.cs', status: 'covered', detail: 'Template-backed.' },
      { component: 'backend/Features/Runner/GlobalOrchestratorBootstrap.cs', status: 'covered', detail: 'Template-backed.' },
      { component: 'backend/Features/Drift/CodePatternDriftAnalysisService.cs', status: 'covered', detail: 'Template-backed.' },
      { component: 'backend/Features/Runner/OrchestratorChat.cs', status: 'pending', detail: 'Still inline.' },
      { component: 'agent-rules/core.md', status: 'pending', detail: 'Not yet registered.' },
    ],
  };
}

function freshDetail(): PromptDetail {
  return {
    name: FRESH,
    title: 'Runner: fresh start',
    description: 'Bootstrap prompt handed to the CLI agent when a task starts from scratch.',
    group: 'Runner',
    promptClass: 'runtime-step',
    hasDefault: true,
    hasOverride: false,
    defaultContent: 'Task {{taskId}} — do {{thing}}.',
    overrideContent: null,
    baseDefaultContent: null,
    effectiveContent: 'Task {{taskId}} — do {{thing}}.',
    defaultSha: 'abc12345def',
    baseDefaultSha: null,
    defaultChangedSinceOverride: false,
    overrideUpdatedAt: null,
    slots: ['taskId', 'thing'],
    usages: [
      { component: 'ProjectRunner', member: 'BuildOrchestratorPrompt', purpose: 'Fresh-start bootstrap prompt.' },
    ],
    lastChangedAt: '2026-07-21T10:00:00Z',
    lastChangedSha: 'abc12345def',
    review: {
      lastReviewedAt: '2026-07-22T10:00:00Z',
      reviewedBy: 'Robert',
      status: 'current',
      findings: [],
    },
    projectOverrides: [],
    calls: calls(12),
    costDisclaimer: 'Theoretical API-equivalent estimate.',
  };
}

function driftDetail(): PromptDetail {
  return {
    name: DRIFT,
    title: 'Aspect: code quality',
    description: 'Review aspect that grades code quality of the change.',
    group: 'Review',
    promptClass: 'runtime-step',
    hasDefault: true,
    hasOverride: true,
    defaultContent: 'Default line v2\nshared',
    overrideContent: 'My override line\nshared',
    baseDefaultContent: 'Default line v1\nshared',
    effectiveContent: 'My override line\nshared',
    defaultSha: 'def67890aaa',
    baseDefaultSha: 'aaa11111bbb',
    defaultChangedSinceOverride: true,
    overrideUpdatedAt: '2026-06-10T12:00:00Z',
    slots: ['diff'],
    usages: [
      { component: 'AspectRunnerService', member: 'RunAspect', purpose: 'Code-quality aspect grading.' },
    ],
    lastChangedAt: '2026-07-20T10:00:00Z',
    lastChangedSha: 'def67890aaa',
    review: null,
    projectOverrides: [{
      projectName: 'Alpha',
      stepId: 'aspect-code-quality',
      promptName: DRIFT,
      content: 'Project override',
      orphaned: false,
      matchesDefault: false,
      addedLines: 1,
      removedLines: 1,
    }],
    calls: calls(),
    costDisclaimer: 'Theoretical API-equivalent estimate.',
  };
}

function previewResult(): PromptPreviewResult {
  return {
    name: FRESH,
    rendered: 'Task ASS-1741 — do {{thing}}.',
    slots: ['taskId', 'thing'],
    filledSlots: ['taskId'],
    missingSlots: ['thing'],
  };
}

class FakePromptAdminService {
  readonly catalog = signal<PromptCatalogResponse | null>(catalog());
  readonly coverage = signal<PromptCoverageResponse | null>(coverage());
  readonly loadError = signal<string | null>(null);

  async loadCatalog(): Promise<void> {
    this.catalog.set(catalog());
  }
  async loadCoverage(): Promise<void> {
    this.coverage.set(coverage());
  }
  getDetail(name: string): Promise<PromptDetail> {
    return Promise.resolve(name === DRIFT ? driftDetail() : freshDetail());
  }
  preview(): Promise<PromptPreviewResult> {
    return Promise.resolve(previewResult());
  }
  saveOverride(name: string): Promise<PromptDetail> {
    return Promise.resolve(name === DRIFT ? driftDetail() : freshDetail());
  }
  resetToDefault(): Promise<PromptDetail> {
    return Promise.resolve(freshDetail());
  }
  rebaseline(): Promise<PromptDetail> {
    return Promise.resolve({ ...driftDetail(), defaultChangedSinceOverride: false });
  }
  review(name: string): Promise<PromptReviewResult> {
    return Promise.resolve({
      name,
      metadata: freshDetail().review!,
      projectOverrides: [],
    });
  }
  reviewAll(): Promise<PromptReviewRunResponse> {
    return Promise.resolve({
      reviewedAt: '2026-07-23T10:00:00Z',
      reviewedCount: 2,
      findingCount: 1,
      results: [],
      orphanedOverrides: [],
    });
  }
}

async function flush(): Promise<void> {
  for (let i = 0; i < 6; i++) await Promise.resolve();
}

async function mount() {
  await TestBed.configureTestingModule({
    imports: [PromptAdminPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: PromptAdminService, useClass: FakePromptAdminService },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(PromptAdminPanelComponent);
  fixture.detectChanges();
  await flush();
  fixture.detectChanges();
  return fixture;
}

describe('PromptAdminPanelComponent', () => {
  it('renders quiet inherited prompts, highlighted overrides, and an override-only filter', async () => {
    const fixture = await mount();
    const host = fixture.nativeElement as HTMLElement;
    const list = host.querySelector<HTMLElement>('[data-testid="prompt-admin-list"]')!;

    expect(host.querySelector('[data-testid="prompt-admin-panel"]')).not.toBeNull();
    expect(host.querySelectorAll('.tree-row').length).toBe(3);

    const groupHeads = Array.from(host.querySelectorAll('.section-header__title')).map(e => e.textContent?.trim());
    expect(groupHeads).toEqual(['Runner', 'Review']);
    expect(host.querySelector('[data-testid="prompt-admin-group-Runner"]')?.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('[data-testid="prompt-admin-home"]')?.classList).toContain('tree-row--active');

    expect(list.textContent).not.toContain('shipped');
    expect(list.textContent).toContain('1 overridden');
    expect(list.textContent).toContain('1 inherited');
    expect(host.querySelector(`[data-testid="prompt-admin-item-${FRESH}"]`)?.closest('app-tree-row')?.classList)
      .toContain('prompt-catalogue__item--inherited');
    expect(host.querySelector(`[data-testid="prompt-admin-item-${FRESH}"]`)?.getAttribute('aria-label'))
      .toBe('Runner: fresh start, inherited');
    expect(host.querySelector(`[data-testid="prompt-admin-item-${DRIFT}"]`)?.getAttribute('aria-label'))
      .toBe('Aspect: code quality, overridden - global');
    expect(host.querySelector(`[data-testid="prompt-admin-override-${DRIFT}"]`)).not.toBeNull();
    expect(host.querySelector(`[data-testid="prompt-admin-override-${DRIFT}"]`)?.textContent)
      .toContain('overridden - global');
    expect(host.querySelector(`[data-testid="prompt-admin-stale-${DRIFT}"]`)).not.toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="prompt-admin-only-overrides"]')!.click();
    fixture.detectChanges();

    expect(host.querySelector(`[data-testid="prompt-admin-item-${FRESH}"]`)).toBeNull();
    expect(host.querySelector(`[data-testid="prompt-admin-item-${DRIFT}"]`)).not.toBeNull();
    expect(host.querySelector('[data-testid="prompt-admin-only-overrides"]')?.getAttribute('aria-pressed')).toBe('true');
  });

  it('collapses prompt groups through the shared section header control', async () => {
    const fixture = await mount();
    const host = fixture.nativeElement as HTMLElement;
    const runnerHead = host.querySelector<HTMLButtonElement>('[data-testid="prompt-admin-group-Runner"]');

    expect(runnerHead).not.toBeNull();
    runnerHead!.click();
    fixture.detectChanges();

    expect(runnerHead!.getAttribute('aria-expanded')).toBe('false');
    expect(host.querySelector(`[data-testid="prompt-admin-item-${FRESH}"]`)).toBeNull();
    expect(host.querySelector(`[data-testid="prompt-admin-item-${DRIFT}"]`)).not.toBeNull();
  });

  it('starts on the landing and opens a prompt with slots + registered usages', async () => {
    const fixture = await mount();
    const host = fixture.nativeElement as HTMLElement;
    const detail = host.querySelector<HTMLElement>('[data-testid="prompt-admin-detail"]')!;

    expect(fixture.componentInstance.selectedName()).toBeNull();
    expect(host.querySelector('[data-testid="prompt-admin-landing"]')).not.toBeNull();
    detail.scrollTop = 120;
    await fixture.componentInstance.select(FRESH);
    fixture.detectChanges();

    expect(detail.scrollTop).toBe(0);
    expect(fixture.componentInstance.selectedName()).toBe(FRESH);
    expect(host.querySelector('.prompts__detail-title')?.textContent).toContain('Runner: fresh start');

    expect(host.querySelector('[data-testid="prompt-admin-slot-taskId"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="prompt-admin-slot-thing"]')).not.toBeNull();

    const usage = host.querySelector('[data-testid="prompt-admin-usage"]');
    expect(usage).not.toBeNull();
    expect(usage!.textContent).toContain('ProjectRunner');
  });

  it('keeps the settings rail usable when an older prompt-detail response omits registry arrays', async () => {
    const fixture = await mount();
    const api = TestBed.inject(PromptAdminService) as unknown as FakePromptAdminService;
    api.getDetail = () => Promise.resolve({
        ...freshDetail(),
        slots: undefined,
        usages: undefined,
      } as unknown as PromptDetail);

    await fixture.componentInstance.select(FRESH);
    expect(() => fixture.detectChanges()).not.toThrow();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('No named slots');
    expect(host.textContent).toContain('No recorded usage');
  });

  it('renders the inline-migration coverage rollup with a covered / total count', async () => {
    const fixture = await mount();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="prompt-admin-coverage"]')).not.toBeNull();
    expect(
      host.querySelector('[data-testid="prompt-admin-coverage-count"]')?.textContent
    ).toContain('4 / 6');
    expect(host.querySelectorAll('[data-testid="prompt-admin-coverage-row-covered"]').length).toBe(4);
    expect(host.querySelectorAll('[data-testid="prompt-admin-coverage-row-pending"]').length).toBe(2);
  });

  it('surfaces the drift banner + diff when the default changed since the override', async () => {
    const fixture = await mount();
    await fixture.componentInstance.select(DRIFT);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="prompt-admin-drift-banner"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="prompt-admin-keep-mine"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="prompt-admin-take-default"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="prompt-admin-diff-view"]')).not.toBeNull();
  });

  it('Probelauf renders the draft and reports the unfilled slot', async () => {
    const fixture = await mount();
    await fixture.componentInstance.select(FRESH);
    await fixture.componentInstance.runPreview();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const result = host.querySelector('[data-testid="prompt-admin-probe-result"]');
    expect(result).not.toBeNull();
    expect(result!.textContent).toContain('Task ASS-1741');
    expect(
      host.querySelector('[data-testid="prompt-admin-probe-missing"]')?.textContent
    ).toContain('thing');
  });
});

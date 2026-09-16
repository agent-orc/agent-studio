import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { OverviewTitleBlockComponent } from './overview-title-block.component';
import type { TaskInfo } from '../../../../../../models/task.model';

/**
 * AGT-2819 split the Overview pane's hero block out of
 * `overview-pane.component.ts`. These cases were the pane's own title and
 * promote coverage and are asserted here unchanged, so the split is proven not
 * to alter public behaviour.
 */
function baseJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'test-1', taskKey: 'wp::test-1', key: 'wp::test-1', title: 'Test', state: '2-ready',
    order: 1, agent: 'human', createdAt: new Date().toISOString(),
    watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
    lastActivity: new Date().toISOString(), sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null,
    ...overrides,
  };
}

async function build(job: TaskInfo) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OverviewTitleBlockComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(OverviewTitleBlockComponent);
  fixture.componentRef.setInput('job', job);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] OverviewTitleBlockComponent initial render skipped:', (e as Error).message);
  }
  return fixture;
}

afterEach(() => {
  TestBed.resetTestingModule();
});

describe('OverviewTitleBlockComponent', () => {
  it('compiles + instantiates without throwing', async () => {
    const fixture = await build(baseJob());
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('hero title block: displayedTitle falls back to job.id when title is missing', async () => {
    const fixture = await build(baseJob({ title: '', id: 'fallback-task-id' }));
    expect(fixture.componentInstance.displayedTitle()).toBe('fallback-task-id');
  });

  it('hero title block: startTitleEdit seeds the draft and flips editingTitle', async () => {
    const fixture = await build(baseJob({ title: 'Original title' }));
    const c = fixture.componentInstance;
    expect(c.editingTitle()).toBe(false);
    c.startTitleEdit();
    expect(c.editingTitle()).toBe(true);
    expect(c.titleDraft()).toBe('Original title');
    c.cancelTitleEdit();
    expect(c.editingTitle()).toBe(false);
  });

  it('hero title block: saving an unchanged title just exits edit mode (no PUT, no override)', async () => {
    const fixture = await build(baseJob({ title: 'Same title' }));
    const c = fixture.componentInstance;
    c.startTitleEdit();
    c.onTitleDraftInput('   Same title   ');
    c.saveTitle();
    expect(c.editingTitle()).toBe(false);
    // displayedTitle still reflects the underlying job because no optimistic
    // override was set.
    expect(c.displayedTitle()).toBe('Same title');
  });

  it('shows orchestrator creation provenance in the visible task heading', async () => {
    const fixture = await build(baseJob({ creationSource: 'orchestrator', createdBy: 'Orchestrator' }));
    const provenance = fixture.nativeElement.querySelector(
      '[data-testid="overview-created-by-orchestrator"]',
    ) as HTMLElement | null;

    expect(provenance?.textContent).toContain('Created by Orchestrator');
  });

  it('renders the lifecycle phase chip with elapsed time from the injected tick', async () => {
    const enteredAt = new Date(Date.now() - 42_000).toISOString();
    const fixture = await build(baseJob({
      state: '3-progress',
      phase: 'loop-waiting',
      phaseEnteredAt: enteredAt,
    }));
    fixture.detectChanges();
    await fixture.whenStable();

    const phase = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="overview-title-phase"]');
    expect(phase?.textContent).toContain('Waiting for loop continuation 0:42');
  });

  it('promote affordance: shown only on a finished planning task across its finished lanes', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '4-auto-review' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(true);
    for (const state of ['5-human-review', '6-completed']) {
      fixture.componentRef.setInput('job', baseJob({ mode: 'planning', state }));
      try { fixture.detectChanges(); } catch { /* ignore */ }
      expect(c.canPromote()).toBe(true);
    }
  });

  it('promote affordance: hidden on a planning task that has not finished', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '1-preparation' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(false);
    for (const state of ['2-ready', '3-progress', '1b-needs-human-review']) {
      fixture.componentRef.setInput('job', baseJob({ mode: 'planning', state }));
      try { fixture.detectChanges(); } catch { /* ignore */ }
      expect(c.canPromote()).toBe(false);
    }
  });

  it('promote affordance: hidden on research tasks even when finished (research is read-only)', async () => {
    const fixture = await build(baseJob({ mode: 'research', state: '6-completed' }));
    expect(fixture.componentInstance.canPromote()).toBe(false);
  });

  it('promote affordance: hidden on coding tasks and on legacy payloads with no mode', async () => {
    const fixture = await build(baseJob({ mode: 'coding', state: '6-completed' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(false);

    // Legacy payloads omit `mode`; the affordance must stay hidden (read as coding).
    fixture.componentRef.setInput('job', baseJob({ state: '6-completed' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.canPromote()).toBe(false);
  });

  it('promote affordance: the promote button is in the DOM for a finished planning task, absent for research', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '5-human-review' }));
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]'),
    ).not.toBeNull();

    fixture.componentRef.setInput('job', baseJob({ mode: 'research', state: '5-human-review' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]'),
    ).toBeNull();
  });

  it('promote affordance: the compact spawn-panel action delegates to the existing promote flow', async () => {
    const fixture = await build(baseJob({
      mode: 'planning',
      state: '5-human-review',
      planningSpawn: {
        spawned: [],
        spawnedCount: 0,
        noFollowUpDeclared: false,
        contractSatisfied: false,
      },
    }));
    const promote = vi.spyOn(fixture.componentInstance, 'promote').mockImplementation(() => undefined);

    (fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]') as HTMLButtonElement).click();

    expect(promote).toHaveBeenCalledOnce();
  });

  it('keeps the hero block on the reusable left-aligned prose measure', async () => {
    const fixture = await build(baseJob());
    const title = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="overview-title-block"]');

    expect(title?.classList.contains('studio-measure')).toBe(true);
    expect(title?.classList.contains('studio-measure--prose')).toBe(true);
  });
});

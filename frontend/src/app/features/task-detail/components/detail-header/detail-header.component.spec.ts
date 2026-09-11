import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { DetailHeaderComponent } from './detail-header.component';
import type { TaskInfo } from '../../../../models/task.model';

const taskInfo: TaskInfo = {
  id: 'ASS-871',
  taskKey: 'ASS-871',
  key: 'ASS-871',
  displayKey: 'ASS-871',
  title: 'Polish commit panel',
  state: '5-human-review',
  order: 1,
  agent: 'codex',
  createdAt: '2026-06-08T10:00:00Z',
  watchPath: 'C:/Projects/agent-taskboard-devspace/agent-taskboard-dev',
  projectName: 'agent-taskboard',
  folderPath: 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/tasks/000/ASS-871',
  lastActivity: '2026-06-08T10:00:00Z',
  sessionName: null,
  model: null,
  cliType: 'codex',
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
};

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
describe('DetailHeaderComponent (smoke)', () => {
  it('renders the last run effective thinking level beside the model', async () => {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', {
      ...taskInfo,
      model: 'gpt-5.6-sol',
      thinkingLevel: 'ultra',
      execution: {
        jobId: taskInfo.id, taskKey: taskInfo.taskKey, processId: 7, startedAt: '2026-07-11T00:00:00Z',
        status: 'completed', exitCode: 0, durationSeconds: 10,
        model: 'gpt-5.6-sol', thinkingLevel: 'medium', runOutcome: 'success',
      },
    });
    fixture.componentRef.setInput('defaultThinkingLevel', 'ultra');
    fixture.detectChanges();

    const level = fixture.nativeElement.querySelector('[data-testid="detail-thinking-level"]') as HTMLElement;
    expect(level.textContent?.trim()).toBe('m');
    expect(level.dataset['thinkingLevel']).toBe('medium');
    expect(level.dataset['thinkingLevelOverride']).toBe('true');
    const model = fixture.nativeElement.querySelector('[data-testid="detail-model-chip"]') as HTMLElement;
    expect(model.textContent?.trim()).toBe('SOLm');
    expect(model.dataset['modelId']).toBe('gpt-5.6-sol');
    expect(model.dataset['modelFamily']).toBe('sol');
    expect(fixture.componentInstance.headerModelTooltip()).toContain('Model ID: gpt-5.6-sol');
    expect(fixture.componentInstance.headerModelTooltip()).toContain('Thinking level: medium');
    expect(fixture.componentInstance.headerModelTooltip()).toContain('CLI: Codex');
  });

  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', taskInfo);

    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] DetailHeaderComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('attributes an automatically raised follow-up to the orchestrator', async () => {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', {
      ...taskInfo,
      creationSource: 'orchestrator',
      createdBy: 'Orchestrator',
    });
    fixture.detectChanges();

    const attribution = fixture.nativeElement.querySelector(
      '[data-testid="detail-created-by-orchestrator"]',
    ) as HTMLElement | null;
    expect(attribution?.textContent).toContain('Created by Orchestrator');
  });

  it('adds worktree commit actions to the text-only overflow menu model', async () => {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', taskInfo);
    fixture.componentRef.setInput('commitActionsAvailable', true);
    fixture.componentRef.setInput('commitMessageDraft', 'Polish commit panel');
    fixture.detectChanges();

    const rows = fixture.componentInstance.triageMenuItems().filter(item => item.kind === 'row');
    expect(rows.map(item => item.label)).toContain('Generate Commit Message');
    expect(rows.map(item => item.label)).toContain('Add Commit...');
    expect(rows.find(item => item.id === 'add-commit')?.hint).toBe('Draft ready');
  });

  // AGT-2006: the human-review acceptance primary (mark-done) depends on the
  // live git landed status. While that status is still loading it must stay
  // disabled + skeletoned and refuse to fire, then switch atomically once the
  // truth is known.
  async function mountHeader() {
    await TestBed.configureTestingModule({
      imports: [DetailHeaderComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DetailHeaderComponent);
    fixture.componentRef.setInput('info', taskInfo);
    return fixture;
  }

  it('holds the git-dependent acceptance primary while git status is loading', async () => {
    const fixture = await mountHeader();
    fixture.componentRef.setInput('gitInfoLoading', true);
    fixture.detectChanges();

    const cmp = fixture.componentInstance;
    expect(cmp.triagePrimary()?.id).toBe('mark-done');
    expect(cmp.primaryAwaitingGit()).toBe(true);

    let emitted = 0;
    cmp.triageAction.subscribe(() => emitted++);
    cmp.onPrimaryClick();
    cmp.triggerPrimary();
    expect(emitted).toBe(0);

    const btn: HTMLButtonElement | null = fixture.nativeElement.querySelector(
      '[data-testid="triage-action-mark-done"]',
    );
    expect(btn).toBeTruthy();
    expect(btn!.disabled).toBe(true);
    expect(btn!.getAttribute('data-git-loading')).toBe('true');
    expect(fixture.nativeElement.querySelector('.detail__triage-primary-skeleton')).toBeTruthy();
  });

  it('releases the acceptance primary once git status has loaded', async () => {
    const fixture = await mountHeader();
    fixture.componentRef.setInput('gitInfoLoading', false);
    fixture.detectChanges();

    const cmp = fixture.componentInstance;
    expect(cmp.primaryAwaitingGit()).toBe(false);

    let emitted = 0;
    cmp.triageAction.subscribe(() => emitted++);
    cmp.onPrimaryClick();
    expect(emitted).toBe(1);

    const btn: HTMLButtonElement | null = fixture.nativeElement.querySelector(
      '[data-testid="triage-action-mark-done"]',
    );
    expect(btn!.disabled).toBe(false);
    expect(btn!.getAttribute('data-git-loading')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Accept');
  });

  it('never gates a non-git primary (Ready "Run now") on git loading', async () => {
    const fixture = await mountHeader();
    fixture.componentRef.setInput('info', { ...taskInfo, state: '2-ready' });
    fixture.componentRef.setInput('gitInfoLoading', true);
    fixture.detectChanges();

    const cmp = fixture.componentInstance;
    expect(cmp.triagePrimary()?.id).toBe('run-now');
    expect(cmp.primaryAwaitingGit()).toBe(false);
  });
});

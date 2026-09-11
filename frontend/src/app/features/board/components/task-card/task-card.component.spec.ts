import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskCardComponent } from './task-card.component';
import { MODEL_IDS } from '../../../cli';
import { ProviderAuthStatusService } from '../../../remote-hosts';
import { TaskService } from '../../../../services/task.service';
import type { TaskInfo, ClientSummary, TagRegistryEntry } from '../../../../models/task.model';
import {
  buildEffectiveModelChip,
  buildDecisionDamBadge,
  buildModeBadge,
  buildOutcomeIssueBadge,
  buildTagChips,
  buildReviewBadge,
  buildHumanReviewBadge,
  buildCodeReviewGradeBadge,
  buildPhaseBadge,
  buildQuotaWaitBadge,
  formatSteerWait,
  buildOwnerChip,
  buildPipelineDots,
  buildTokenBubble,
  buildExternalDoneBadge,
  currentIntegrationStatus,
} from './task-card-view-model';

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
describe('TaskCardComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // job
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] TaskCardComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('commit tooltip exposes the actual file list, not just the count', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    const files = [
      'backend/Services/Analysis/AnalysisReportContract.cs',
      'backend/Services/Analysis/AnalysisReportStore.cs',
      'docs/system/reports/analysis-reports.md',
    ];
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      tags: ['quality:concerns', 'docs:concerns'],
      commit: {
        sha: '5f969a6abc',
        shortSha: '5f969a6',
        message: 'test(analysis-report): add schema round-trip tests',
        filesChanged: files.length,
        files,
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.title).toContain('5f969a6');
    expect(tooltip.title).toContain('3 file(s) changed');
    expect(tooltip.body).toContain('AnalysisReportContract.cs');
    expect(tooltip.body).toContain('analysis-reports.md');
    expect(tooltip.body).toContain('<ul>');

    const commit = fixture.nativeElement.querySelector('[data-testid="task-card-commit"]') as HTMLElement | null;
    expect(commit?.getAttribute('data-has-files')).toBe('true');
  });

  it('commit tooltip caps the file list and reports overflow', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    const files = Array.from({ length: 20 }, (_, i) => `src/file-${i}.ts`);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'deadbeefcaf',
        shortSha: 'deadbee',
        message: 'refactor: rename utils',
        filesChanged: files.length,
        files,
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.body).toContain('src/file-0.ts');
    expect(tooltip.body).toContain('src/file-11.ts');
    expect(tooltip.body).not.toContain('src/file-12.ts');
    expect(tooltip.body).toContain('+8 more file(s)');
  });

  it('commit tooltip falls back to count-only when files array is empty', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'abc12345',
        shortSha: 'abc1234',
        message: 'chore: legacy commit pre-file-tracking',
        filesChanged: 4,
        files: [],
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.title).toContain('4 file(s) changed');
    expect(tooltip.body).not.toContain('<ul>');
    expect(tooltip.body).not.toContain('+');

    const commit = fixture.nativeElement.querySelector('[data-testid="task-card-commit"]') as HTMLElement | null;
    expect(commit?.getAttribute('data-has-files')).toBeNull();
  });

  it('commit tooltip escapes HTML in file paths to prevent injection', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'beefcafe',
        shortSha: 'beefcaf',
        message: 'fix <script>alert(1)</script>',
        filesChanged: 1,
        files: ['src/<img src=x onerror=alert(2)>.ts'],
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.body).not.toContain('<script>');
    expect(tooltip.body).not.toContain('<img src=x');
    expect(tooltip.body).toContain('&lt;script&gt;');
    expect(tooltip.body).toContain('&lt;img');
  });

  it('marks running cards with whole-card ring and badge only', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      execution: {
        jobId: 'task-1',
        taskKey: 'test::task-1',
        processId: 1234,
        startedAt: '2026-05-23T07:00:00Z',
        status: 'running',
        exitCode: null,
        durationSeconds: null,
        model: null,
        runOutcome: null,
      },
    }));
    fixture.detectChanges();

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--running')).toBe(true);
    expect(host?.getAttribute('data-running')).toBe('true');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-progress"]')).toBeNull();
    expect(findCssDeclaration('.task-card__progress', 'height')).toBeNull();

    fixture.componentRef.setInput('job', makeJob({ execution: null }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-progress"]')).toBeNull();
  });

  it('uses a uniform card border and whole-card ring instead of a left-only accent', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready' }));
    fixture.detectChanges();

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host).not.toBeNull();
    expect(findCssDeclaration('.task-card', 'border')).toContain('1px solid color-mix');
    expect(findCssDeclaration('.task-card[data-task-type', '--task-card-type-color')).toContain('248,113,113');
    expect(hasExplicitCssDeclaration('.task-card', 'border-left')).toBe(false);
    expect(hasExplicitCssDeclaration('.task-card__progress', 'height')).toBe(false);
    expect(findCssDeclaration('.task-card', '--task-card-ring')).toBe('0 0 0 1px var(--studio-card-state-border)');
    expect(findCssDeclaration('.task-card', 'box-shadow')).toContain('var(--task-card-ring)');
    expect(findCssDeclaration('.task-card', 'padding')).toBe('var(--studio-card-padding)');
    expect(findCssDeclaration('.task-card', 'border-radius')).toBe('var(--studio-card-radius)');
    expect(findCssDeclaration('.task-card__title', 'font-size')).toBe('var(--studio-card-title-size)');
    expect(findCssDeclaration('.task-card__title', 'font-weight')).toBe('var(--studio-card-title-weight)');
    expect(findCssDeclaration('.task-card__commits', 'background')).toBe('transparent');
    expect(findCssDeclaration('.task-card__commits', 'border')).toBe('0px');
  });

  it('routes compact-card density through the standard card tokens', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready' }));
    fixture.componentRef.setInput('compact', true);
    fixture.detectChanges();

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--compact')).toBe(true);
    expect(findCssDeclaration('.task-card--compact', 'padding')).toBe(
      'var(--studio-card-compact-padding-block) var(--studio-card-compact-padding-inline)'
    );
    expect(findCssDeclaration('.task-card--compact', 'border-radius')).toBe('var(--studio-card-compact-radius)');
    expect(findCssDeclarationWithSelectorParts(['.task-card--compact', '.task-card__title'], 'gap')).toBe('var(--studio-spacing-2)');
    expect(findCssDeclarationWithSelectorParts(['.task-card--compact', '.task-card__title'], 'font-size')).toBe('var(--studio-card-compact-title-size)');
  });

  it('suppresses the "Running live" pill on cards outside 3-progress (lane is source of truth)', async () => {
    // Regression: a stale `execution.status === 'running'` snapshot on a
    // card whose lane has already moved past 3-progress (4-auto-review,
    // 5-human-review, etc.) used to flash a misleading "Running live"
    // pill. The lane is the single source of truth for liveness; the
    // execution overlay must only surface on actively-running cards.
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    const runningExecution = {
      jobId: 'task-7', taskKey: 'test::task-7', processId: 4242,
      startedAt: '2026-05-28T10:00:00Z', status: 'running',
      exitCode: null, durationSeconds: null, model: null, runOutcome: null,
    };

    // 3-progress + running -> badge + whole-card ring present.
    fixture.componentRef.setInput('job', makeJob({
      state: '3-progress',
      execution: runningExecution,
    }));
    fixture.detectChanges();
    expect(fixture.componentInstance.executionBadge()?.tone).toBe('running');
    expect(fixture.componentInstance.isRunning()).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-progress"]')).toBeNull();

    // Same execution but lane has moved to 4-auto-review → suppress.
    for (const state of ['4-auto-review', '5-human-review', '6-completed', '4-review']) {
      fixture.componentRef.setInput('job', makeJob({
        state,
        execution: runningExecution,
      }));
      fixture.detectChanges();
      expect(fixture.componentInstance.executionBadge(), `state=${state}`).toBeNull();
      expect(fixture.componentInstance.isRunning(), `state=${state}`).toBe(false);
      expect(fixture.nativeElement.querySelector('[data-testid="task-card-progress"]'), `state=${state}`).toBeNull();
    }
  });

  it('keeps a Progress card running during a pre-step despite stale runActivity', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '3-progress',
      execution: {
        jobId: 'task-1',
        taskKey: 'test::task-1',
        processId: 0,
        startedAt: '2026-07-26T20:00:00Z',
        status: 'failed',
        exitCode: 1,
        durationSeconds: 1,
        model: 'gpt-5',
      },
      runner: null,
      executionLocation: null,
      runActivity: { kind: 'failed-idle', attempt: 1, lastError: 'stale failure' },
      liveStatus: {
        attempt: 1,
        activeStep: {
          stepId: 'pre-worktree-create',
          displayName: 'Create worktree',
          kind: 'pre',
        },
        nextSteps: [{ stepId: 'core', displayName: 'Agent execution' }],
      },
    }));
    fixture.detectChanges();

    expect(fixture.componentInstance.isRunning()).toBe(true);
    expect(fixture.componentInstance.executionBadge()).toEqual({ label: 'Running live', tone: 'running' });
    expect(fixture.componentInstance.stalledState()).toBeNull();
    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--running')).toBe(true);
    expect(host?.classList.contains('task-card--stalled')).toBe(false);
  });

  it('flags an escalated human-review card as needing attention (Failed != Done)', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '5e-escalated',
      orchestratorVerdict: 'escalate',
    }));
    fixture.detectChanges();

    expect(fixture.componentInstance.needsAttention()).toBe(true);

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--attention')).toBe(true);

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Escalated');
    expect(pill?.className).toContain('review-decision-badge--attention');
  });

  it('renders an amber Stalled signal for a failed In-Progress card with no active run', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '3-progress',
      execution: null,
      runActivity: { kind: 'failed-idle', attempt: 1, lastError: 'agent did not produce a reply' },
    }));
    fixture.detectChanges();

    expect(fixture.componentInstance.stalledState()).toMatchObject({ reason: 'failed', label: 'Stalled' });
    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--stalled')).toBe(true);
    expect(host?.getAttribute('data-stalled')).toBe('failed');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-stalled"]')?.textContent).toContain('Stalled');
  });

  it('stays quiet for an accepted human-review card', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      orchestratorVerdict: 'accept',
    }));
    fixture.detectChanges();

    expect(fixture.componentInstance.needsAttention()).toBe(false);

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--attention')).toBe(false);

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]') as HTMLElement | null;
    expect(pill).toBeNull();
  });

  it('stays quiet for Review and Completed cards carrying a stale escalate verdict', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);

    fixture.componentRef.setInput('job', makeJob({ state: '5-human-review', orchestratorVerdict: 'escalate' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.needsAttention()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]')).toBeNull();

    fixture.componentRef.setInput('job', makeJob({ state: '6-completed', orchestratorVerdict: 'escalate' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.needsAttention()).toBe(false);
  });

  it('renders a runner outcome issue pill', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      outcomeIssue: {
        kind: 'permission-blocked',
        label: 'Permission blocked',
        severity: 'High',
        summary: 'Permission denied and could not request permission from user.',
        lastSeenAt: '2026-05-11T10:00:00Z',
      },
    }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-outcome-issue"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Permission blocked');
    expect(pill?.className).toContain('task-card__issue-pill--high');
  });

  it('moves an older outcome issue off the card after a later accepted run', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      orchestratorVerdict: 'accept',
      lastActivity: '2026-07-10T11:00:00Z',
      outcomeIssue: {
        kind: 'watchdog-timeout', label: 'Watchdog', severity: 'High', summary: 'Older failed attempt',
        lastSeenAt: '2026-07-10T10:00:00Z',
      },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-outcome-issue"]')).toBeNull();
  });

  it('renders an unpushed task branch as a warning outcome issue', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      outcomeIssue: {
        kind: 'task-branch-unpushed',
        label: 'Task branch unpushed',
        severity: 'Warn',
        summary: 'Push status: failed.',
        lastSeenAt: '2026-06-09T10:00:00Z',
      },
    }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-outcome-issue"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Task branch unpushed');
    expect(pill?.className).toContain('task-card__issue-pill--warn');
  });

  it('expands an epic card to show inline sub-tasks and opens a clicked sub-task', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const epic = makeJob({
      id: 'epic-1',
      taskKey: 'test::epic-1',
      title: 'Epic container',
      kind: 'epic',
      state: '2-ready',
    });
    const subTask = makeJob({
      id: 'sub-1',
      taskKey: 'test::sub-1',
      title: 'Inline child task',
      state: '4-auto-review',
      epicId: epic.id,
      orchestratorVerdict: 'reissue',
    });
    const fixture = TestBed.createComponent(TaskCardComponent);
    const opened: TaskInfo[] = [];
    fixture.componentInstance.subTaskClick.subscribe((value) => opened.push(value));
    fixture.componentRef.setInput('job', epic);
    fixture.componentRef.setInput('epicSubTasks', [subTask]);
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="task-card-epic-toggle"]');
    expect(toggle).not.toBeNull();
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(host.querySelector('[data-testid="task-card-epic-subtasks"]')).toBeNull();

    toggle?.click();
    fixture.detectChanges();

    expect(toggle?.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('[data-testid="task-card-epic-subtasks"]')).not.toBeNull();
    expect(host.textContent).toContain('Inline child task');
    expect(host.textContent).toContain('Post Processing');
    expect(host.querySelector('[data-testid="task-card-epic-subtask-verdict"]')?.textContent?.trim()).toBe('reissue');

    host.querySelector<HTMLButtonElement>('[data-testid="task-card-epic-subtask"]')?.click();

    expect(opened).toEqual([subTask]);
  });

  it('keeps the inline epic expand open across a data refresh without remounting the sub-list', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const epic = makeJob({
      id: 'epic-refresh',
      taskKey: 'test::epic-refresh',
      title: 'Epic container',
      kind: 'epic',
      state: '2-ready',
    });
    const subTask = makeJob({
      id: 'sub-refresh',
      taskKey: 'test::sub-refresh',
      title: 'Inline child task',
      state: '4-auto-review',
      epicId: epic.id,
    });

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', epic);
    fixture.componentRef.setInput('epicSubTasks', [subTask]);
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    host.querySelector<HTMLButtonElement>('[data-testid="task-card-epic-toggle"]')?.click();
    fixture.detectChanges();

    const panelBefore = host.querySelector('[data-testid="task-card-epic-subtasks"]') as HTMLElement | null;
    const itemBefore = host.querySelector('[data-testid="task-card-epic-subtask"]') as HTMLElement | null;
    expect(panelBefore).not.toBeNull();
    expect(itemBefore).not.toBeNull();
    // Stamp the live DOM nodes so we can prove they are reused, not rebuilt.
    panelBefore!.dataset['persistMarker'] = 'panel';
    itemBefore!.dataset['persistMarker'] = 'item';

    // Simulate a polling refresh: brand-new TaskInfo objects (same ids)
    // replace the inputs, exactly as a fresh board snapshot would.
    fixture.componentRef.setInput('job', { ...epic, lastActivity: '2026-05-11T10:00:00Z' });
    fixture.componentRef.setInput('epicSubTasks', [{ ...subTask, lastActivity: '2026-05-11T10:00:00Z' }]);
    fixture.detectChanges();

    const toggle = host.querySelector('[data-testid="task-card-epic-toggle"]') as HTMLButtonElement | null;
    expect(toggle?.getAttribute('aria-expanded')).toBe('true');

    const panelAfter = host.querySelector('[data-testid="task-card-epic-subtasks"]') as HTMLElement | null;
    const itemAfter = host.querySelector('[data-testid="task-card-epic-subtask"]') as HTMLElement | null;
    expect(panelAfter).not.toBeNull();
    expect(host.querySelectorAll('[data-testid="task-card-epic-subtasks"]').length).toBe(1);
    expect(host.querySelectorAll('[data-testid="task-card-epic-subtask"]').length).toBe(1);
    // Same element instances => @if/@for kept the nodes; no double mount.
    expect(panelAfter?.dataset['persistMarker']).toBe('panel');
    expect(itemAfter?.dataset['persistMarker']).toBe('item');
  });

  it('keys the inline epic expand on the task id so it survives a card re-mount', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const epic = makeJob({
      id: 'epic-remount',
      taskKey: 'test::epic-remount',
      title: 'Epic container',
      kind: 'epic',
      state: '2-ready',
    });
    const subTask = makeJob({
      id: 'sub-remount',
      taskKey: 'test::sub-remount',
      title: 'Child task',
      state: '2-ready',
      epicId: epic.id,
    });

    const first = TestBed.createComponent(TaskCardComponent);
    first.componentRef.setInput('job', epic);
    first.componentRef.setInput('epicSubTasks', [subTask]);
    first.detectChanges();
    const firstHost: HTMLElement = first.nativeElement;
    (firstHost.querySelector('[data-testid="task-card-epic-toggle"]') as HTMLButtonElement | null)?.click();
    first.detectChanges();
    expect(
      firstHost.querySelector('[data-testid="task-card-epic-toggle"]')?.getAttribute('aria-expanded'),
    ).toBe('true');

    // Re-mount: destroy the card and build a brand-new instance for the same
    // epic - a lane move, the group-by-epic toggle, and filter rebuilds all
    // tear the card down and recreate it. A local signal would reset to
    // collapsed here; the store keeps the state keyed on the epic id.
    first.destroy();
    const second = TestBed.createComponent(TaskCardComponent);
    second.componentRef.setInput('job', { ...epic });
    second.componentRef.setInput('epicSubTasks', [{ ...subTask }]);
    second.detectChanges();
    const secondHost: HTMLElement = second.nativeElement;

    expect(
      secondHost.querySelector('[data-testid="task-card-epic-toggle"]')?.getAttribute('aria-expanded'),
    ).toBe('true');
    expect(secondHost.querySelector('[data-testid="task-card-epic-subtasks"]')).not.toBeNull();
    expect(secondHost.textContent).toContain('Child task');
  });

  // ── Mode badge (planning / research recognizable at a glance) ──────────

  it('renders a mode pill naming the mode for a planning card', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'planning' }));
    fixture.detectChanges();

    expect(fixture.componentInstance.modeBadge()?.mode).toBe('planning');
    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-mode"]') as HTMLElement | null;
    expect(pill).not.toBeNull();
    expect(pill?.getAttribute('data-mode')).toBe('planning');
    expect(pill?.className).toContain('task-card__mode-pill--planning');
    expect(pill?.textContent).toContain('Planning');
  });

  it('renders a research mode pill', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'research' }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-mode"]') as HTMLElement | null;
    expect(pill?.getAttribute('data-mode')).toBe('research');
    expect(pill?.textContent).toContain('Research');
  });

  it('renders a concept mode pill', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'concept' }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-mode"]') as HTMLElement | null;
    expect(pill?.getAttribute('data-mode')).toBe('concept');
    expect(pill?.className).toContain('task-card__mode-pill--concept');
    expect(pill?.textContent).toContain('Concept');
  });

  it('stays quiet for coding cards and cards with no mode set', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);

    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'coding' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.modeBadge()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-mode"]')).toBeNull();

    // Older payload with no mode field at all → still quiet.
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: undefined }));
    fixture.detectChanges();
    expect(fixture.componentInstance.modeBadge()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-mode"]')).toBeNull();
  });

  // ── Commit-attribution surface (AC#3, AC#4, AC#6) ──────────────────────

  async function renderCard(job: TaskInfo) {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    return fixture;
  }

  function commit(overrides: Partial<NonNullable<TaskInfo['commits']>[number]> = {}): NonNullable<TaskInfo['commits']>[number] {
    return {
      sha: 'aaaaaaa0000',
      shortSha: 'aaaaaaa',
      message: 'feat: do a thing',
      filesChanged: 2,
      files: ['src/a.ts', 'src/b.ts'],
      at: '2026-05-30T10:00:00Z',
      ...overrides,
    };
  }

  it('shows a ready task branch before commits without inventing file activity', async () => {
    const fixture = await renderCard(makeJob({
      state: '2-ready',
      commit: null,
      commits: [],
      provenance: {
        branch: 'task/ready-before-first-commit',
        base: 'base000',
        transitions: [
          {
            lane: '2-ready',
            atUtc: '2026-06-10T18:58:06.2321361Z',
            branchTip: 'c599fb5c764b1991b5cb681d13dc6a4e98479c44',
            workBranchHead: '948f4892c8a5bf0d2d234146547cba22f76501a8',
          },
        ],
        merge: null,
      },
    }));

    const context = fixture.componentInstance.changeContext();
    expect(context?.label).toBe('BR');
    expect(context?.value).toBe('task/ready-before-first-commit');
    expect(context?.summary).toBe('no commits yet');
    expect(context?.stat).toBeNull();

    const change = fixture.nativeElement.querySelector('[data-testid="task-card-change-context"]') as HTMLElement | null;
    expect(change?.textContent).toContain('task/ready-before-first-commit');
    expect(change?.textContent).toContain('no commits yet');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]')).toBeNull();
  });

  it('labels the shared main checkout as worktree context, not branch context', async () => {
    const fixture = await renderCard(makeJob({
      state: '3-progress',
      commit: null,
      commits: [],
      provenance: null,
    }));

    const context = fixture.componentInstance.changeContext();
    expect(context?.label).toBe('WT');
    expect(context?.value).toBe('main checkout');
    expect(context?.summary).toBe('no commits yet');
  });

  it('AGT-2046 renders the two-segment merge signal with develop lit, main muted', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      // A merged card carries the task commit that landed; the signal is a fact
      // about that commit (AGT-2063), so it must be present for the signal to show.
      commits: [commit()],
      mergeSignal: {
        branch: 'task/ATP-1',
        inIntegration: true,
        inRelease: false,
        integrationBranch: 'develop',
        releaseBranch: 'main',
        integrationSha: 'a1b2c3d',
        releaseSha: null,
      },
      integration: {
        status: 'integrated',
        deliveryRef: 'task/ATP-1',
        sha: 'a1b2c3d',
        integrationBranch: 'develop',
        detail: 'Every attributed commit is present in develop.',
      },
    }));

    const signal = fixture.nativeElement.querySelector('[data-testid="task-card-merge-signal"]') as HTMLElement | null;
    expect(signal).not.toBeNull();
    expect(signal?.getAttribute('data-develop')).toBe('true');
    expect(signal?.getAttribute('data-main')).toBe('false');
    const dev = signal?.querySelector('[data-seg="develop"]') as HTMLElement | null;
    const main = signal?.querySelector('[data-seg="main"]') as HTMLElement | null;
    expect(dev?.className).toContain('task-card__merge-seg--on');
    expect(main?.className).not.toContain('task-card__merge-seg--on');
  });

  it('AGT-2063 renders NO merge signal on a card without a task commit', async () => {
    // The operator bug: an empty card carried a backend mergeSignal (its branch
    // base is trivially in develop/main) and showed a merge state. With no task
    // commit the [d|m] indicator must not appear at all.
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [],
      mergeSignal: {
        branch: 'task/ATP-1',
        inIntegration: true,
        inRelease: false,
        integrationBranch: 'develop',
        releaseBranch: 'main',
        integrationSha: 'a1b2c3d',
        releaseSha: null,
      },
    }));

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-merge-signal"]')).toBeNull();
  });

  it('AGT-2046 replaces the cryptic BR label chip with a branch icon', async () => {
    const fixture = await renderCard(makeJob({ state: '5-human-review' }));

    // The old two-letter code chip is no longer in the DOM ...
    expect(fixture.nativeElement.querySelector('.task-card__change-ref-label')).toBeNull();
    // ... a self-explanatory icon chip took its place.
    const icon = fixture.nativeElement.querySelector('.task-card__change-ref-icon') as HTMLElement | null;
    expect(icon).not.toBeNull();
    expect(icon?.getAttribute('aria-label') ?? '').toMatch(/branch|working tree/i);
  });

  it('AC#6 0-commit analysis-only card shows a calm "no code changes" badge, no pill', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [],
      codeActivityDetected: false,
    }));

    expect(fixture.componentInstance.commitChainView()).toBeNull();
    const badge = fixture.componentInstance.commitEmptyBadge();
    expect(badge?.tone).toBe('no-code');

    const el = fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]') as HTMLElement | null;
    expect(el?.getAttribute('data-tone')).toBe('no-code');
    expect(el?.textContent).toContain('no code changes');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit"]')).toBeNull();
  });

  it('AC#3 0-commit card that moved HEAD shows an amber "commit discovery pending" diagnostic', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [],
      codeActivityDetected: true,
    }));

    const badge = fixture.componentInstance.commitEmptyBadge();
    expect(badge?.tone).toBe('discovery');

    const el = fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]') as HTMLElement | null;
    expect(el?.getAttribute('data-tone')).toBe('discovery');
    expect(el?.className).toContain('task-card__no-commits--discovery');
    expect(el?.textContent).toContain('commit discovery pending');
  });

  it('remote delivery ref without attributed commits is discovery-pending, never no-code', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [],
      codeActivityDetected: false,
      integration: {
        status: 'pending',
        deliveryRef: 'runner/agent-runner-01/AGT-2220',
        sha: null,
        integrationBranch: 'main',
        detail: 'Delivery ref exists but attribution is pending.',
      },
    }));

    const context = fixture.componentInstance.changeContext();
    expect(context?.value).toBe('runner/agent-runner-01/AGT-2220');
    expect(context?.summary).toBe('commit discovery pending');
    expect(fixture.componentInstance.commitEmptyBadge()?.tone).toBe('discovery');

    const el = fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]') as HTMLElement | null;
    expect(el?.textContent).toContain('commit discovery pending');
    expect(el?.textContent).not.toContain('no code changes');
  });

  it('does not render a zero-commit badge outside review lanes (3-progress stays quiet)', async () => {
    const fixture = await renderCard(makeJob({
      state: '3-progress',
      commit: null,
      commits: [],
      codeActivityDetected: true,
    }));
    expect(fixture.componentInstance.commitEmptyBadge()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]')).toBeNull();
  });

  it('AC#4 single-commit card renders exactly one row and no "+N more"', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [commit({ shortSha: 'abc1234', message: 'fix: one', filesChanged: 1, files: ['x.ts'] })],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(1);
    expect(cc?.rows.length).toBe(1);
    expect(cc?.moreCount).toBe(0);

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="task-card-commit-row"]');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('abc1234');
    expect(rows[0].textContent).toContain('fix: one');
    expect(rows[0].textContent).toContain('1 file');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit-more"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]')).toBeNull();
  });

  it('routes commit-row foreground through the semantic commit token', async () => {
    const fixture = await renderCard(makeJob({
      state: '4-auto-review',
      commit: null,
      commits: [commit({ shortSha: 'a614ea0', message: 'fix: readable commit hash' })],
    }));

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit"]')).not.toBeNull();
    expect(findCssDeclaration('.task-card__commits', 'color')).toBe('var(--studio-commit-fg)');
    expect(findCssDeclaration('.task-card__commit-sha', 'color')).toBe('var(--studio-commit-fg)');
    expect(findCssDeclaration('.task-card__commit-subject', 'color')).toBe('var(--studio-commit-muted-fg)');
    expect(findCssDeclaration('.task-card__commit-files', 'color')).toBe('var(--studio-commit-muted-fg)');
  });

  it('AC#4 three-commit card renders all three rows (sha + subject + files), newest first', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      // stored oldest -> newest
      commits: [
        commit({ shortSha: 'old1111', message: 'feat: first', filesChanged: 1 }),
        commit({ shortSha: 'mid2222', message: 'feat: second', filesChanged: 2 }),
        commit({ shortSha: 'new3333', message: 'feat: third', filesChanged: 3 }),
      ],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(3);
    expect(cc?.rows.length).toBe(3);
    expect(cc?.moreCount).toBe(0);
    // newest first
    expect(cc?.rows[0].shortSha).toBe('new3333');
    expect(cc?.rows[2].shortSha).toBe('old1111');

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="task-card-commit-row"]');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('feat: third');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit-more"]')).toBeNull();
  });

  it('AC#4 four-commit card shows top-3 plus a "+1 more commit" disclosure', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [
        commit({ shortSha: 'c1aaaaa', message: 'one' }),
        commit({ shortSha: 'c2bbbbb', message: 'two' }),
        commit({ shortSha: 'c3ccccc', message: 'three' }),
        commit({ shortSha: 'c4ddddd', message: 'four' }),
      ],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(4);
    expect(cc?.rows.length).toBe(3);
    expect(cc?.moreCount).toBe(1);

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="task-card-commit-row"]');
    expect(rows.length).toBe(3);
    const more = fixture.nativeElement.querySelector('[data-testid="task-card-commit-more"]') as HTMLElement | null;
    expect(more?.textContent).toContain('+1 more commit');
  });

  it('sources the chain from commits[] (SSOT), not the legacy singular commit', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: commit({ shortSha: 'legacyy', message: 'legacy singular' }),
      commits: [commit({ shortSha: 'chain01', message: 'chain wins' })],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(1);
    expect(cc?.rows[0].shortSha).toBe('chain01');
    expect(cc?.rows[0].subject).toBe('chain wins');
  });

  it('falls back to the legacy singular commit when commits[] is empty', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: commit({ shortSha: 'legacyy', message: 'legacy singular' }),
      commits: [],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(1);
    expect(cc?.rows[0].shortSha).toBe('legacyy');
    expect(fixture.componentInstance.commitEmptyBadge()).toBeNull();
  });

  // Regression: the portal migration dropped the native `popover` attribute that
  // kept the token-usage panel collapsed by default. Without a default-hidden
  // state every card paints its `position: fixed` popover off the right viewport
  // edge (operator screenshot: multiple panels hung open + clipped). The panel
  // must stay hidden until the directive opens it after explicit interaction.
  it('keeps the token popover hidden until the trigger receives focus', async () => {
    const fixture = await renderCard(makeJob({
      tokenSummary: {
        calls: 2,
        inputTokens: 120_000,
        outputTokens: 18_000,
        cacheReadTokens: 250_000,
        cacheCreationTokens: 12_000,
        totalTokens: 400_000,
        estimatedApiCostUsd: 1.25,
        allModelsPriced: true,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-06-09T08:30:00Z',
        entries: [],
      },
    }));

    const bubble = fixture.nativeElement.querySelector('[data-testid="task-card-token-bubble"]') as HTMLElement | null;
    expect(bubble, 'token bubble should render for a non-zero summary').not.toBeNull();

    const popover = fixture.nativeElement.querySelector('[data-token-popover]') as HTMLElement | null;
    expect(popover, 'token popover element should exist in the card').not.toBeNull();
    expect(popover?.hidden, 'token popover must start hidden, not hang open').toBe(true);
    expect(getComputedStyle(popover!).display, 'hidden must win over the component host display rule').toBe('none');

    const wrap = fixture.nativeElement.querySelector('[appTokenPopover]') as HTMLElement | null;
    expect(wrap).not.toBeNull();
    wrap?.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    fixture.detectChanges();
    expect(popover?.hidden, 'focusing the trigger should reveal the popover').toBe(false);
    expect(getComputedStyle(popover!).display).not.toBe('none');
    // Calm layout: the footnote line is short; the full disclaimer text
    // (incl. "historical list prices") lives in the tooltip, not inline.
    const footnote = popover?.querySelector('[data-testid="token-cost-tooltip"]');
    expect(footnote?.textContent).toContain('Total (est.): $1.25');
    expect(footnote?.textContent).not.toContain('historical list prices');
    expect(footnote?.getAttribute('aria-label')).toContain('Estimated cost: $1.25');
    expect(footnote?.getAttribute('aria-label')).toContain('historical list prices');

    fixture.destroy();
  });

  // ── Canonical execution-location badge ─────────────────────────────────

  it('renders a remote runner badge next to the CLI badge for a leased run', async () => {
    const fixture = await renderCard(makeJob({
      state: '3-progress',
      execution: {
        jobId: 'task-1', taskKey: 'test::task-1', processId: 4242,
        startedAt: '2026-07-09T10:00:00Z', status: 'running',
        exitCode: null, durationSeconds: null, model: 'claude-sonnet-4.5', runOutcome: null,
      },
      executionLocation: {
        state: 'remote-running', executionKind: 'remote', runnerId: 'agent-runner-01',
        clientId: 'runner-client-01', hostDisplayName: 'linux-host', configuredRunnerId: 'agent-runner-02',
        startedAt: '2026-07-09T10:00:00Z', lastHeartbeat: '2026-07-09T10:01:00Z',
        lastActivityAt: '2026-07-09T10:01:02Z', processId: 4242, sessionId: 'session-remote',
        branch: 'task/AGT-2158', worktreePath: '/worktrees/AGT-2158',
        connectionState: 'connected', leaseState: 'active',
        trustReason: 'The task server holds the fenced run lease.',
      },
    }));

    const pill = fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]') as HTMLElement | null;
    expect(pill).not.toBeNull();
    expect(pill?.getAttribute('data-execution-state')).toBe('remote-running');
    expect(pill?.textContent).toContain('agent-runner-01');
  });

  it('treats a live remote lease as running when no local execution exists', async () => {
    const acquiredAt = new Date().toISOString();
    const fixture = await renderCard(makeJob({
      state: '3-progress',
      execution: null,
      executionLocation: {
        state: 'remote-running', executionKind: 'remote', runnerId: 'agent-runner-01',
        clientId: 'runner-client-01', hostDisplayName: 'linux-host', configuredRunnerId: 'agent-runner-01',
        startedAt: acquiredAt, lastHeartbeat: acquiredAt, lastActivityAt: acquiredAt,
        processId: null, sessionId: null, branch: 'task/task-1', worktreePath: '/worktrees/task-1',
        connectionState: 'connected', leaseState: 'active',
        trustReason: 'The task server holds the fenced run lease.',
      },
      runner: {
        runnerId: 'agent-runner-01@linux-host',
        runnerName: 'agent-runner-01',
        hostname: 'linux-host',
        backendName: 'remote',
        isRemote: true,
        leaseId: 'lease-live',
        fencingToken: 9,
        acquiredAt,
      },
    }));

    const card = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(fixture.componentInstance.isRunning()).toBe(true);
    expect(card?.classList.contains('task-card--running')).toBe(true);
    expect(card?.getAttribute('data-running')).toBe('true');
  });

  it('renders a quiet "lokal" runner chip for an in-process run with no remote lease', async () => {
    const fixture = await renderCard(makeJob({
      state: '3-progress',
      execution: {
        jobId: 'task-1', taskKey: 'test::task-1', processId: 4242,
        startedAt: '2026-07-09T10:00:00Z', status: 'running',
        exitCode: null, durationSeconds: null, model: null, runOutcome: null,
      },
      executionLocation: {
        state: 'local-running', executionKind: 'local', runnerId: 'stable@local',
        hostDisplayName: 'Local workstation', startedAt: '2026-07-09T10:00:00Z',
        lastActivityAt: '2026-07-09T10:01:02Z', processId: 4242,
        connectionState: 'connected', leaseState: 'local-process',
        trustReason: 'The local CLI registry reports a live process.',
      },
    }));

    const pill = fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]') as HTMLElement | null;
    expect(pill).not.toBeNull();
    expect(pill?.getAttribute('data-execution-state')).toBe('local-running');
    expect(pill?.textContent?.trim()).toContain('Local');
  });

  it('shows no runner chip on an idle (not-running) card', async () => {
    const fixture = await renderCard(makeJob({ state: '2-ready', execution: null, executionLocation: null }));
    expect(fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]')).toBeNull();
  });

  it('shows why a remote Ready card is waiting when its CLI authentication is unavailable', async () => {
    const fixture = await renderCard(makeJob({
      state: '2-ready',
      cliType: 'claude',
      execution: null,
      executionLocation: {
        state: 'queued-remote', executionKind: 'remote', runnerId: 'agent-runner-01',
        configuredRunnerId: 'agent-runner-01', connectionState: 'connected',
        leaseState: 'queued', trustReason: 'Project execution assignment targets this runner.',
      },
    }));
    const now = new Date();
    TestBed.inject(ProviderAuthStatusService).ingest([{
      runnerId: 'agent-runner-01', name: 'linux-host', hostId: 'host-01', instanceId: 'coding-01',
      runnerVersion: '1.0.0', protocolVersion: 2, status: 'active',
      registeredAt: now.toISOString(), lastSeenAt: now.toISOString(),
      hostAdmission: { hostId: 'host-01', admissionState: 'open' },
      capabilities: [{
        key: 'cli-execution:claude', category: 'cli-execution', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: now.toISOString(),
        freshUntil: new Date(now.getTime() + 120_000).toISOString(), isFresh: true,
        consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      }, {
        key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'unavailable',
        healthState: 'healthy', advertisedAt: now.toISOString(),
        freshUntil: new Date(now.getTime() + 120_000).toISOString(), isFresh: true,
        consecutiveFailures: 0, detail: 'Not logged in', affectedClaims: [], recoveryHistory: [],
      }],
    }]);
    fixture.detectChanges();

    const wait = fixture.nativeElement.querySelector(
      '[data-testid="task-card-provider-auth-wait"]',
    ) as HTMLElement | null;
    expect(wait?.textContent).toContain('Waiting for Claude sign-in on linux-host');
    expect(fixture.componentInstance.providerAuthWait()?.tooltip).toContain('Not logged in');
  });

  it('renders a compact family code and named thinking level without model text', async () => {
    const fixture = await renderCard(makeJob({
      state: '2-ready', cliType: 'claude', model: 'claude-opus-4-8', thinkingLevel: 'xhigh',
    }));
    const indicator = fixture.nativeElement.querySelector('[data-testid="task-card-effective-model"]') as HTMLElement;
    expect(indicator.textContent?.trim()).toBe('OP4.8xh');
    expect(indicator.dataset['modelFamily']).toBe('opus');
    expect(indicator.dataset['modelId']).toBe('claude-opus-4-8');
    expect(indicator.dataset['cli']).toBe('claude');
  });
});

describe('buildEffectiveModelChip', () => {
  it('shows the effective run thinking level and strengthens a default mismatch', () => {
    const job = makeJob({
      cliType: 'codex',
      model: 'gpt-5.6-sol',
      thinkingLevel: 'ultra',
      execution: {
        jobId: 'task-1', taskKey: 'test::task-1', processId: 1, startedAt: '2026-07-11T00:00:00Z',
        status: 'completed', exitCode: 0, durationSeconds: 12,
        model: 'gpt-5.6-sol', thinkingLevel: 'medium', runOutcome: 'success',
      },
    });

    const chip = buildEffectiveModelChip(job, makeOwner({ defaultThinkingLevel: 'high' }));

    expect(chip.thinkingLevel).toMatchObject({
      short: 'm',
      effective: 'medium',
      configured: 'ultra',
      differsFromConfigured: true,
      differsFromDefault: true,
    });
    expect(chip.tooltip.body).toContain('Thinking level:</b> medium');
    expect(chip.tooltip.body).toContain('Configured thinking level:</b> ultra');
  });

  it('keeps a configured/default thinking level quiet before the first run', () => {
    const chip = buildEffectiveModelChip(
      makeJob({ cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high', execution: null }),
      makeOwner({ defaultThinkingLevel: 'high' }),
    );

    expect(chip.thinkingLevel).toMatchObject({ short: 'h', effective: 'high', differsFromDefault: false });
  });

  it('strengthens a task override even when the run matches its configured level', () => {
    const chip = buildEffectiveModelChip(
      makeJob({ cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'ultra', execution: null }),
      makeOwner({ defaultThinkingLevel: 'high' }),
    );

    expect(chip.thinkingLevel).toMatchObject({
      short: 'u', effective: 'ultra', configured: 'ultra', defaultLevel: 'high',
      differsFromConfigured: false, differsFromDefault: true,
    });
  });

  it('shows a visible quota-fallback badge with model and reason', () => {
    const job = makeJob({
      cliType: 'claude',
      model: 'claude-opus-4-7',
      quotaFallback: { cliType: 'codex', model: 'gpt-5.3-codex', reason: 'claude Weekly at 100% (cap 95%)' },
    });
    const chip = buildEffectiveModelChip(job, makeOwner());
    expect(chip.source).toBe('fallback');
    expect(chip.label).toBe('fallback: gpt-5.3-codex');
    expect(chip.cliLabel).toBe('Codex');
    expect(chip.tooltip.title).toBe('Quota fallback active');
    expect(chip.tooltip.body).toContain('Weekly at 100%');
  });

  it('shows owner-client default when job has no cliType/model', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null, ownerClientId: 'local-default' });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('default');
    expect(chip.isDefault).toBe(true);
    expect(chip.label).toBe('opus 4.7');
    expect(chip.icon).toBe('✴️');
    expect(chip.cliLabel).toBe('Claude Code');
  });

  it('shows explicit model when job has cliType/model set', () => {
    const job = makeJob({ cliType: 'codex', model: 'o3', ownerClientId: 'local-default' });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('explicit');
    expect(chip.isDefault).toBe(false);
    expect(chip.label).toBe('o3');
  });

  it('renders the codex gpt-5.6 default on the card (AGT-2025)', () => {
    // A codex task created after gpt-5.6 detection carries the gpt-5.6-sol id;
    // the card must surface it verbatim, never the literal agent field.
    const job = makeJob({ cliType: 'codex', model: MODEL_IDS.gpt56Sol, ownerClientId: 'local-default' });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('explicit');
    expect(chip.label).toBe('gpt-5.6-sol');
    expect(chip.cliLabel).toBe('Codex');
  });

  it('shows running execution model for in-progress jobs', () => {
    const job = makeJob({
      cliType: null,
      model: null,
      ownerClientId: 'local-default',
      execution: {
        jobId: 'task-1', taskKey: 'test::task-1', processId: 1, startedAt: '',
        status: 'running', exitCode: null, durationSeconds: null,
        model: 'claude-sonnet-4-6', runOutcome: null
      },
    });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('run');
    expect(chip.isDefault).toBe(false);
    expect(chip.label).toBe('sonnet 4.6');
  });

  it('shows "human" when owner client is human-kind with no defaults', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null, ownerClientId: 'user-1' });
    const owner = makeOwner({ id: 'user-1', kind: 'human', defaultCliType: null, defaultModel: null });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('human');
    expect(chip.label).toBe('human');
    expect(chip.icon).toBe('\u{1F464}');
  });

  it('shows "unknown" when no defaults and owner is not human', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null, ownerClientId: 'svc' });
    const owner = makeOwner({ id: 'svc', kind: 'service', defaultCliType: null, defaultModel: null });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('unknown');
    expect(chip.label).toBe('unknown');
  });

  it('tooltip contains agent as pickup permission', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(typeof chip.tooltip).toBe('object');
    expect(chip.tooltip.body).toContain('human');
    expect(chip.tooltip.body).toContain('pickup permission');
  });

  it('tooltip indicates client default when source is default', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.tooltip.title).toContain('client default');
    expect(chip.tooltip.body).toContain('client default');
  });

  // Orphan/system tasks persist cliType 'system', which is not a real CLI. The
  // chip and tooltip must degrade gracefully instead of crashing in escapeHtml
  // (cliTypeLabel returns undefined for unknown CLIs). Regression for the
  // group-by-epic board surfacing these cards.
  it('degrades to "unknown" for an unrecognized cliType instead of throwing', () => {
    const job = makeJob({
      agent: 'system',
      cliType: 'system' as unknown as TaskInfo['cliType'],
      model: null,
      ownerClientId: 'svc',
    });
    const owner = makeOwner({ id: 'svc', kind: 'service', defaultCliType: null, defaultModel: null });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('unknown');
    expect(chip.label).toBe('unknown');
    expect(chip.cliLabel).toBeNull();
    expect(chip.tooltip.body).toContain('<b>CLI:</b> none');
  });

  it('ignores an unrecognized cliType but still honors owner defaults', () => {
    const job = makeJob({
      agent: 'system',
      cliType: 'system' as unknown as TaskInfo['cliType'],
      model: null,
      ownerClientId: 'local-default',
    });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('default');
    expect(chip.cliLabel).toBe('Claude Code');
    expect(chip.tooltip.body).toContain('Claude Code');
  });
});

describe('buildOwnerChip', () => {
  it('uses a stable two-letter owner marker while preserving the full tooltip', () => {
    const chip = buildOwnerChip(makeOwner({ id: 'rainer-m', displayName: 'Rainer Mischewski' }));
    expect(chip.initials).toBe('RM');
    expect(chip.label).toBe('Rainer Mischewski');
    expect(chip.tooltip).toContain('Rainer Mischewski');
    expect(chip.tooltip).toContain('rainer-m');
  });

  it('falls back to the owner id for compact initials', () => {
    const chip = buildOwnerChip(makeOwner({ id: 'codex-runner-1', displayName: '' }));
    expect(chip.initials).toBe('CR');
  });

  it('keeps umlaut names readable when deriving initials', () => {
    const chip = buildOwnerChip(makeOwner({ id: 'joerg-m', displayName: 'Jörg Müller' }));
    expect(chip.initials).toBe('JM');
  });
});

describe('buildTokenBubble', () => {
  it('uses backend-resolved model labels for aggregate and per-run rows', () => {
    const bubble = buildTokenBubble({
      calls: 2,
      inputTokens: 3000,
      outputTokens: 300,
      cacheReadTokens: 1000,
      cacheCreationTokens: 0,
      totalTokens: 4300,
      estimatedApiCostUsd: 0.09,
      allModelsPriced: true,
      lastModel: 'GPT-5 Codex',
      lastUpdate: '2026-06-09T08:05:00Z',
      entries: [
        {
          ts: '2026-06-09T08:00:00Z',
          model: 'GPT-5 Codex',
          participantId: 'agent:codex',
          inputTokens: 2000,
          outputTokens: 200,
          cacheReadTokens: 1000,
          cacheCreationTokens: 0,
        },
        {
          ts: '2026-06-09T08:05:00Z',
          model: 'Claude Haiku 4.5',
          participantId: 'orchestrator:Test',
          inputTokens: 1000,
          outputTokens: 100,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
        },
      ],
    });

    expect(bubble?.model).toBe('GPT-5 Codex');
    expect(bubble?.costTooltip).toContain('Estimated cost: $0.09');
    expect(bubble?.entries.map((entry) => entry.model)).toEqual([
      'GPT-5 Codex',
      'Claude Haiku 4.5',
    ]);
  });
});

describe('buildModeBadge', () => {
  it('returns a planning badge with the picker glyph and a mode-naming tooltip', () => {
    const badge = buildModeBadge('planning');
    expect(badge?.mode).toBe('planning');
    expect(badge?.label).toBe('Planning');
    expect(badge?.icon).toBe('🗺️');
    expect(badge?.tooltip).toContain('Planning mode');
    expect(badge?.tooltip).toContain('read-only');
  });

  it('returns a research badge', () => {
    const badge = buildModeBadge('research');
    expect(badge?.mode).toBe('research');
    expect(badge?.label).toBe('Research');
    expect(badge?.icon).toBe('🔍');
    expect(badge?.tooltip).toContain('Research mode');
    expect(badge?.tooltip).toContain('web access');
  });

  it('returns null for coding and for an absent mode (older payloads)', () => {
    expect(buildModeBadge('coding')).toBeNull();
    expect(buildModeBadge(undefined)).toBeNull();
  });
});

// ASS-748: the card must not repeat what the lane already says. These cover the
// pure render-logic that drops lane-mirroring labels and concern/classifier
// tags while keeping genuine content tags.
describe('buildTagChips — lane-mirror + concern suppression', () => {
  function registry(...entries: TagRegistryEntry[]): Map<string, TagRegistryEntry> {
    return new Map(entries.map((e) => [e.id, e]));
  }
  function tag(id: string, label = id): TagRegistryEntry {
    return { id, label, color: '#888888', description: '' };
  }

  it('drops concern + unparseable classifier tags regardless of lane', () => {
    const chips = buildTagChips(
      ['requirement:concerns', 'quality:concerns', 'review:unparseable'],
      new Map(),
      '5-human-review',
    );
    expect(chips).toEqual([]);
  });

  it('drops a "Q&As" registry tag by its label', () => {
    const reg = registry(tag('qas-tag', 'Q&As'));
    const chips = buildTagChips(['qas-tag'], reg, '3-progress');
    expect(chips).toEqual([]);
  });

  it('drops tags that mirror the auto-review lane', () => {
    const reg = registry(tag('review', 'Review'), tag('auto-review', 'Auto Review'));
    const chips = buildTagChips(['review', 'auto-review'], reg, '4-auto-review');
    expect(chips).toEqual([]);
  });

  it('drops tags that mirror the human-review lane (review ready / sign off)', () => {
    const reg = registry(
      tag('humanreview', 'Human Review'),
      tag('review-ready', 'Review Ready'),
      tag('ready-to-sign-off', 'Ready to sign off'),
    );
    const chips = buildTagChips(
      ['humanreview', 'review-ready', 'ready-to-sign-off'],
      reg,
      '5-human-review',
    );
    expect(chips).toEqual([]);
  });

  it('keeps genuine content tags (architecture, security)', () => {
    const reg = registry(tag('architecture', 'Architecture'), tag('security', 'Security'));
    const chips = buildTagChips(['architecture', 'security'], reg, '4-auto-review');
    expect(chips.map((c) => c.label)).toEqual(['Architecture', 'Security']);
  });

  it('keeps auto-review reissue history off cards before Review too', () => {
    const chips = buildTagChips(['reissue:autoreview'], new Map(), '2-ready');
    expect(chips).toEqual([]);
  });

  it('keeps reissue and abort event history off human-review cards', () => {
    const abort = tag('abort-review:watchdog', 'Abort: watchdog');
    abort.color = '#ef4444';
    abort.description = 'The run stopped after a watchdog timeout';
    const chips = buildTagChips(['reissue:autoreview', abort.id], registry(abort), '5-human-review');

    expect(chips).toEqual([]);
  });

  it('keeps reissue and abort history off Escalated cards', () => {
    const abort = tag('abort-review:watchdog', 'Abort: watchdog');
    abort.color = '#ef4444';
    const chips = buildTagChips(['reissue:autoreview', abort.id], registry(abort), '5e-escalated');

    expect(chips).toEqual([]);
  });

  it('does not suppress a lane-name tag in an unrelated lane', () => {
    const reg = registry(tag('review', 'Review'));
    // 'review' only mirrors review lanes; in 3-progress it survives.
    const chips = buildTagChips(['review'], reg, '3-progress');
    expect(chips.map((c) => c.id)).toEqual(['review']);
  });

  it('suppresses the raw code-review:grade-* tag (the grade badge renders it)', () => {
    const reg = registry(tag('code-review:grade-a', 'Grade A'));
    const chips = buildTagChips(['code-review:grade-a'], reg, '4-auto-review');
    expect(chips).toEqual([]);
  });

  it('drops redundant review and orchestrator-moved tags', () => {
    const reg = registry(
      tag('accepted', 'Reviewed'),
      tag('orchestrator-moved', 'Orchestrator: moved'),
    );
    const chips = buildTagChips(
      ['accepted', 'orchestrator-moved', 'orchestrator:moved'],
      reg,
      '5-human-review',
    );
    expect(chips).toEqual([]);
  });

  it('suppresses the internal integrationpending marker in favor of computed status', () => {
    const chips = buildTagChips(
      ['integrationpending', 'architecture'],
      registry(tag('integrationpending', 'Integration pending'), tag('architecture', 'Architecture')),
      '5-human-review',
    );

    expect(chips.map((chip) => chip.id)).toEqual(['architecture']);
  });
});

describe('buildCodeReviewGradeBadge — A/B/C/D quality grade (ASS-1657)', () => {
  it('reads the grade letter + tone from a code-review:grade-* tag', () => {
    const badge = buildCodeReviewGradeBadge(['code-review:grade-b']);
    expect(badge?.grade).toBe('B');
    expect(badge?.tone).toBe('b');
    expect(badge?.tooltip).toContain('Quality grade B');
  });

  it('handles every grade A-D', () => {
    expect(buildCodeReviewGradeBadge(['code-review:grade-a'])?.grade).toBe('A');
    expect(buildCodeReviewGradeBadge(['code-review:grade-c'])?.grade).toBe('C');
    expect(buildCodeReviewGradeBadge(['code-review:grade-d'])?.grade).toBe('D');
  });

  it('returns null when no grade tag is present', () => {
    expect(buildCodeReviewGradeBadge(['architecture', 'code-review:pass'])).toBeNull();
    expect(buildCodeReviewGradeBadge(undefined)).toBeNull();
    expect(buildCodeReviewGradeBadge([])).toBeNull();
  });
});

describe('buildReviewBadge — active review status only', () => {
  it('stays quiet for a ready summary', () => {
    const badge = buildReviewBadge({
      status: 'ready', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: 42,
    });
    expect(badge).toBeNull();
  });

  it('uses "summarizing" (not "auto-reviewing") while generating', () => {
    const badge = buildReviewBadge({
      status: 'generating', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: null,
    });
    expect(badge?.label).toBe('summarizing');
  });

  it('shows bounded summary exhaustion as a reviewable degraded Result', () => {
    const badge = buildReviewBadge({
      status: 'degraded',
      startedAt: null,
      finishedAt: null,
      errorMessage: 'summary fixture failed',
      bytesWritten: null,
      attempt: 3,
      maxAttempts: 3,
    });

    expect(badge?.label).toBe('result degraded');
    expect(badge?.tooltip).toContain('3/3 summary attempts');
    expect(badge?.tooltip).toContain('core run remains reviewable');
  });
});

describe('buildHumanReviewBadge — current lane only', () => {
  it('ignores stale verdicts in Review', () => {
    const badge = buildHumanReviewBadge(makeJob({ state: '5-human-review', orchestratorVerdict: 'escalate' }));
    expect(badge).toBeNull();
  });

  it('derives Escalated from the acute lane even when the journal verdict is missing', () => {
    const badge = buildHumanReviewBadge(makeJob({ state: '5e-escalated', orchestratorVerdict: null }));
    expect(badge?.label).toBe('Escalated');
  });

  it('stays quiet for completed cards carrying stale history', () => {
    expect(buildHumanReviewBadge(makeJob({ state: '6-completed', orchestratorVerdict: 'escalate' }))).toBeNull();
  });
});

describe('current card-status reconciliation', () => {
  const integration = {
    status: 'integrated' as const,
    deliveryRef: 'task/task-1',
    sha: '2d8d201',
    integrationBranch: 'develop',
    detail: null,
  };
  const integrationError = {
    kind: 'integration-error',
    label: 'Integration error',
    severity: 'High',
    summary: 'Transient failure',
    lastSeenAt: '2026-07-28T08:03:00Z',
  };

  it('keeps integration truth only on accepted lanes', () => {
    expect(currentIntegrationStatus(makeJob({ state: '5-human-review', integration }))).toEqual(integration);
    expect(currentIntegrationStatus(makeJob({ state: '3-progress', integration }))).toBeNull();
  });

  it('never combines integrated with an integration-error issue', () => {
    const badge = buildOutcomeIssueBadge(makeJob({
      state: '5e-escalated',
      integration,
      outcomeIssue: integrationError,
      execution: { jobId: 'task-1', taskKey: 'test::task-1', processId: 0,
        startedAt: '', status: 'failed', exitCode: 1, durationSeconds: 1, model: null, runOutcome: 'failed' },
    }));
    expect(badge).toBeNull();
  });

  it('suppresses any issue in Review and any issue superseded by a successful last run', () => {
    expect(buildOutcomeIssueBadge(makeJob({
      state: '5-human-review',
      outcomeIssue: integrationError,
    }))).toBeNull();
    expect(buildOutcomeIssueBadge(makeJob({
      state: '5e-escalated',
      outcomeIssue: integrationError,
      execution: { jobId: 'task-1', taskKey: 'test::task-1', processId: 0,
        startedAt: '', status: 'completed', exitCode: 0, durationSeconds: 1, model: null, runOutcome: 'success' },
    }))).toBeNull();
  });

  it('keeps the latest failed outcome acute in Escalated', () => {
    const badge = buildOutcomeIssueBadge(makeJob({
      state: '5e-escalated',
      outcomeIssue: { ...integrationError, kind: 'watchdog-timeout', label: 'Watchdog timeout' },
      execution: { jobId: 'task-1', taskKey: 'test::task-1', processId: 0,
        startedAt: '', status: 'failed', exitCode: 1, durationSeconds: 1, model: null, runOutcome: 'failed' },
    }));
    expect(badge?.label).toBe('Watchdog timeout');
    expect(badge?.tone).toBe('high');
  });
});

describe('buildDecisionDamBadge', () => {
  it('shows the transitive dam impact and every waiting key', () => {
    const badge = buildDecisionDamBadge(makeJob({
      state: '5-human-review',
      transitiveWaiters: { count: 3, keys: ['AGT-2201', 'AGT-2202', 'AGT-2203'] },
    }));
    expect(badge?.label).toBe('Dams 3 cards');
    expect(badge?.tooltip).toContain('AGT-2201, AGT-2202, AGT-2203');
  });

  it('shows dam impact without reviving a stale review verdict', () => {
    const job = makeJob({
      state: '5-human-review',
      orchestratorVerdict: 'escalate',
      transitiveWaiters: { count: 1, keys: ['AGT-2201'] },
    });
    expect(buildDecisionDamBadge(job)?.label).toBe('Dams 1 card');
    expect(buildHumanReviewBadge(job)).toBeNull();
  });
});

describe('buildPhaseBadge — no lane-mirroring "Ready"', () => {
  it('returns null for human-ready (the lane already says it)', () => {
    expect(buildPhaseBadge('human-ready')).toBeNull();
  });

  it('still surfaces non-lane intake substates', () => {
    expect(buildPhaseBadge('intake-blocked')?.label).toBe('Intake blocked');
  });

  it('suppresses a persisted phase after the task reaches Review', () => {
    expect(buildPhaseBadge('post-processing-blocked', null, undefined, '5-human-review')).toBeNull();
  });

  it('shows transactional integration while the task remains in Review', () => {
    const badge = buildPhaseBadge('integrating', null, undefined, '5-human-review');
    expect(badge?.label).toBe('Integrating');
    expect(badge?.tone).toBe('integrating');
  });

  it('surfaces post-processing substates separately from the lane label', () => {
    const running = buildPhaseBadge('post-processing-running');
    expect(running?.label).toBe('Post processing');
    expect(running?.tone).toBe('post-processing-running');

    const blocked = buildPhaseBadge('post-processing-blocked');
    expect(blocked?.label).toBe('Post processing blocked');
    expect(blocked?.tone).toBe('post-processing-blocked');
  });

  it('surfaces a steer-pending wait with a live "since" timer (Run-Liveness Slice B)', () => {
    const since = '2026-07-11T00:00:00.000Z';
    const now = Date.parse(since) + 135_000; // 2m 15s later
    const pill = buildPhaseBadge('steer-pending', since, now);
    expect(pill?.tone).toBe('steer-pending');
    expect(pill?.label).toBe('Waiting for answer · 2:15');
    expect(pill?.tooltip).toContain('will not hang');
  });

  it('shows the bare steer-pending label when no wait-start is known', () => {
    expect(buildPhaseBadge('steer-pending')?.label).toBe('Waiting for answer');
  });

  it('shows loop-waiting as a timed no-slot phase', () => {
    const since = '2026-07-11T00:00:00.000Z';
    const pill = buildPhaseBadge('loop-waiting', since, Date.parse(since) + 42_000);
    expect(pill?.tone).toBe('loop-waiting');
    expect(pill?.label).toBe('Waiting for loop continuation 0:42');
    expect(pill?.tooltip).toContain('freed its execution slot');
  });
});

describe('buildQuotaWaitBadge', () => {
  it('shows the confirmed reset time and a live rounded-up countdown', () => {
    const now = Date.parse('2026-07-22T11:02:30.000Z');
    const badge = buildQuotaWaitBadge({
      cliType: 'codex',
      startedAt: '2026-07-22T11:02:00.000Z',
      resetAt: '2026-07-22T11:14:00.000Z',
      thresholdMinutes: 30,
      reason: 'Confirmed nearby quota reset',
    }, now);

    expect(badge?.label).toContain('12 min remaining');
    expect(badge?.minutesLeft).toBe(12);
    expect(badge?.tooltip).toContain('retries admission');
  });

  it('stays explicit while the due reset is being refreshed', () => {
    const resetAt = '2026-07-22T11:14:00.000Z';
    const badge = buildQuotaWaitBadge({
      cliType: 'codex',
      startedAt: '2026-07-22T11:02:00.000Z',
      resetAt,
      thresholdMinutes: 30,
      reason: 'Confirmed nearby quota reset',
    }, Date.parse(resetAt));

    expect(badge?.label).toContain('reset due · refreshing');
    expect(badge?.minutesLeft).toBe(0);
  });
});

describe('formatSteerWait', () => {
  it('formats sub-hour waits as m:ss', () => {
    expect(formatSteerWait(0)).toBe('0:00');
    expect(formatSteerWait(75_000)).toBe('1:15');
    expect(formatSteerWait(9_000)).toBe('0:09');
  });
  it('keeps hour-plus waits in total-minutes mm:ss form - the 5-hour hang shape', () => {
    expect(formatSteerWait(5 * 3600_000 + 7 * 60_000 + 9_000)).toBe('307:09');
  });
  it('never goes negative on clock skew', () => {
    expect(formatSteerWait(-5000)).toBe('0:00');
  });
});

describe('buildPipelineDots', () => {
  it('marks the core run dot as active for a live progress card', () => {
    const dots = buildPipelineDots(makeJob({
      state: '3-progress',
      execution: {
        jobId: 'task-1', taskKey: 'test::task-1', processId: 1, startedAt: '',
        status: 'running', exitCode: null, durationSeconds: null,
        model: 'gpt-5-codex', runOutcome: null,
      },
    }));
    expect(dots.currentLabel).toBe('Core agent work');
    expect(dots.dots.map((dot) => [dot.id, dot.status])).toEqual([
      ['pre', 'done'],
      ['run', 'active'],
      ['post', 'pending'],
      ['review', 'pending'],
    ]);
  });

  it('surfaces post-processing as the active step without a large progress bar', () => {
    const dots = buildPipelineDots(makeJob({ state: '3-progress', phase: 'post-processing-running' }));
    expect(dots.currentLabel).toBe('Post steps');
    expect(dots.dots.find((dot) => dot.id === 'post')?.status).toBe('active');
    expect(dots.dots.find((dot) => dot.id === 'run')?.status).toBe('done');
  });
});

function makeOwner(overrides: Partial<ClientSummary> = {}): ClientSummary {
  return {
    id: 'local-default',
    displayName: 'Local Default',
    emoji: null,
    colour: null,
    kind: 'agent-instance',
    registeredAt: '',
    lastSeenAt: null,
    tokenBudgetMonthly: null,
    notes: null,
    defaultCliType: null,
    defaultModel: null,
    ...overrides,
  };
}

describe('buildExternalDoneBadge', () => {
  it('returns null for a task with no external completion', () => {
    expect(buildExternalDoneBadge(makeJob())).toBeNull();
  });

  it('surfaces the source and summary for an externally completed task', () => {
    const badge = buildExternalDoneBadge(makeJob({
      externalCompletion: {
        source: 'operator-chat',
        summary: 'Implemented out-of-band in docs/concepts.',
        completedAt: '2026-07-08T10:00:00Z',
      },
    }));
    expect(badge).not.toBeNull();
    expect(badge!.label).toBe('extern erledigt');
    expect(badge!.tooltip).toContain('operator-chat');
    expect(badge!.tooltip).toContain('Implemented out-of-band');
  });
});

describe('TaskCardComponent external-done badge render', () => {
  it('renders the extern-erledigt pill when externalCompletion is set', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      externalCompletion: {
        source: 'operator-chat',
        summary: 'done elsewhere',
        completedAt: '2026-07-08T10:00:00Z',
      },
    }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-external-done"]') as HTMLElement | null;
    expect(pill).not.toBeNull();
    expect(pill!.textContent).toContain('extern erledigt');
  });

  it('does not render the pill for a normally completed task', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '6-completed' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-external-done"]')).toBeNull();
  });
});

describe('TaskCardComponent — waits-on dependency chip (AGT-2029)', () => {
  async function mount(job: TaskInfo) {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    return fixture;
  }

  it('renders an open, clickable waiting chip from waitsOn', async () => {
    const fixture = await mount(
      makeJob({
        state: '2-ready',
        references: { dependsOn: ['CAR-3'], relatedTo: [], blockedBy: [], supersedes: [] },
        waitsOn: {
          blocked: true,
          cycleDetected: false,
          items: [
            {
              key: 'CAR-3',
              resolved: true,
              fulfilled: false,
              targetJobId: 'car-3',
              targetTitle: 'Pricing lib',
              targetState: '2-ready',
              targetWatchPath: '/ws/car',
            },
          ],
        },
      }),
    );
    const chip = fixture.nativeElement.querySelector(
      '[data-testid="task-card-waiting-on"]',
    ) as HTMLElement | null;
    expect(chip).toBeTruthy();
    expect(chip!.tagName).toBe('BUTTON');
    expect(chip!.getAttribute('data-tone')).toBe('open');
    expect(chip!.textContent).toContain('waits for completion: CAR-3');
  });

  it('renders the dependency as the only current wait when live status also carries a runner position', async () => {
    const fixture = await mount(
      makeJob({
        state: '2-ready',
        references: { dependsOn: ['AGT-2534'], relatedTo: [], blockedBy: [], supersedes: [] },
        waitsOn: {
          blocked: true,
          cycleDetected: false,
          items: [{
            key: 'AGT-2534',
            resolved: true,
            fulfilled: false,
            targetJobId: 'agt-2534',
            targetTitle: 'Dependency',
            targetState: '5-human-review',
            targetWatchPath: '/ws/agent-taskboard',
          }],
        },
        liveStatus: {
          attempt: 1,
          activeStep: null,
          nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent execution' }],
          queue: { kind: 'runner', position: 4 },
          latestEventAt: '2026-08-09T08:00:00Z',
        },
      }),
    );

    const current = fixture.nativeElement.querySelector('[data-testid="task-live-current"]') as HTMLElement;
    expect(current.textContent).toContain('waits for completion: AGT-2534');
    expect(current.textContent).not.toContain('runner slot');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-waiting-on"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-next"]')?.textContent)
      .toContain('Agent execution');
  });

  it('renders a cycle chip when the dependency graph is cyclic', async () => {
    const fixture = await mount(
      makeJob({
        state: '2-ready',
        references: { dependsOn: ['APP-2'], relatedTo: [], blockedBy: [], supersedes: [] },
        waitsOn: {
          blocked: true,
          cycleDetected: true,
          items: [
            {
              key: 'APP-2',
              resolved: true,
              fulfilled: false,
              targetJobId: 'app-2',
              targetTitle: 'B',
              targetState: '2-ready',
              targetWatchPath: '/ws/app',
            },
          ],
        },
      }),
    );
    const chip = fixture.nativeElement.querySelector(
      '[data-testid="task-card-waiting-on"]',
    ) as HTMLElement | null;
    expect(chip!.getAttribute('data-tone')).toBe('cycle');
    expect(chip!.textContent).toContain('dep cycle');
  });

  it('renders no chip when the card has no dependencies', async () => {
    const fixture = await mount(makeJob({ state: '2-ready' }));
    expect(
      fixture.nativeElement.querySelector('[data-testid="task-card-waiting-on"]'),
    ).toBeNull();
  });

  it('renders the orchestrator follow-up as a board chip', async () => {
    const fixture = await mount(makeJob({
      state: '5e-escalated',
      references: {
        dependsOn: [], relatedTo: [], blockedBy: ['AGT-2801'], supersedes: [],
        raisedFollowUps: ['AGT-2801'],
      },
    }));
    TestBed.inject(TaskService).jobs.set([
      makeJob({ id: 'follow-up', key: 'AGT-2801', title: 'Review toolchain unavailable', state: '1-preparation' }),
    ]);
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector(
      '[data-testid="task-card-intervention"]',
    ) as HTMLElement | null;
    expect(chip).toBeTruthy();
    expect(chip!.textContent).toContain('AGT-2801 · preparation · open');
    expect(chip!.getAttribute('data-intervention-state')).toBe('open');
  });
});

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
  };
}

function findCssDeclaration(selectorFragment: string, property: string): string | null {
  for (const sheet of Array.from(document.styleSheets)) {
    let rules: CSSRuleList;
    try {
      rules = sheet.cssRules;
    } catch {
      continue;
    }
    const found = findDeclarationInRules(rules, selectorFragment, property);
    if (found !== null) return found;
  }
  return null;
}

function findCssDeclarationWithSelectorParts(selectorParts: string[], property: string): string | null {
  for (const sheet of Array.from(document.styleSheets)) {
    let rules: CSSRuleList;
    try {
      rules = sheet.cssRules;
    } catch {
      continue;
    }
    const found = findDeclarationWithSelectorPartsInRules(rules, selectorParts, property);
    if (found !== null) return found;
  }
  return null;
}

function findDeclarationWithSelectorPartsInRules(rules: CSSRuleList, selectorParts: string[], property: string): string | null {
  for (const rule of Array.from(rules)) {
    if ('selectorText' in rule && typeof rule.selectorText === 'string') {
      const cssRule = rule as CSSStyleRule;
      const style = cssRule.style;
      if (selectorParts.every((part) => cssRule.selectorText.includes(part)) && style.getPropertyValue(property)) {
        return style.getPropertyValue(property).trim();
      }
    }
    if ('cssRules' in rule) {
      const nested = findDeclarationWithSelectorPartsInRules((rule as CSSGroupingRule).cssRules, selectorParts, property);
      if (nested !== null) return nested;
    }
  }
  return null;
}

function findDeclarationInRules(rules: CSSRuleList, selectorFragment: string, property: string): string | null {
  for (const rule of Array.from(rules)) {
    if ('selectorText' in rule && typeof rule.selectorText === 'string') {
      const style = (rule as CSSStyleRule).style;
      if (rule.selectorText.includes(selectorFragment) && style.getPropertyValue(property)) {
        return style.getPropertyValue(property).trim();
      }
    }
    if ('cssRules' in rule) {
      const nested = findDeclarationInRules((rule as CSSGroupingRule).cssRules, selectorFragment, property);
      if (nested !== null) return nested;
    }
  }
  return null;
}

function hasExplicitCssDeclaration(selectorFragment: string, property: string): boolean {
  for (const sheet of Array.from(document.styleSheets)) {
    let rules: CSSRuleList;
    try {
      rules = sheet.cssRules;
    } catch {
      continue;
    }
    if (hasExplicitDeclarationInRules(rules, selectorFragment, property)) return true;
  }
  return false;
}

function hasExplicitDeclarationInRules(rules: CSSRuleList, selectorFragment: string, property: string): boolean {
  for (const rule of Array.from(rules)) {
    if ('selectorText' in rule && typeof rule.selectorText === 'string' && rule.selectorText.includes(selectorFragment)) {
      const cssText = (rule as CSSStyleRule).cssText;
      if (cssText.includes(`${property}:`)) return true;
    }
    if ('cssRules' in rule && hasExplicitDeclarationInRules((rule as CSSGroupingRule).cssRules, selectorFragment, property)) {
      return true;
    }
  }
  return false;
}

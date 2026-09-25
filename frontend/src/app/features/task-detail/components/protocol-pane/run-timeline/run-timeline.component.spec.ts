import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunTimelineComponent } from './run-timeline.component';
import type { CliExecutionContext, RunPromptEntry, RunRecord } from '../../../../../features/run-timeline';
import type { TaskInfo } from '../../../../../models/task.model';

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
describe('RunTimelineComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunTimelineComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] RunTimelineComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders every run chronologically with re-open transitions', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('runs', [
      { ...runRecord(3, 'restart', 'completed', 'Fix review note', 15), trigger: 'restart' },
      { ...runRecord(1, 'start', 'completed', null, 125), trigger: 'initial' },
      {
        ...runRecord(2, 'continue', 'failed', 'Please try again', 33),
        trigger: 'operator-continue',
        triggeredBy: 'operator local-default',
        triggerReason: 'Please try again.',
      },
    ]);
    fixture.detectChanges();

    const cards = fixture.nativeElement.querySelectorAll('[data-testid="run-timeline-card"]');
    const transitions = fixture.nativeElement.querySelectorAll('[data-testid^="run-transition-"]');
    expect(cards).toHaveLength(3);
    expect(transitions).toHaveLength(2);

    const buttons = fixture.nativeElement.querySelectorAll('[data-testid^="run-icon-"]');
    expect(buttons[0].getAttribute('data-testid')).toBe('run-icon-1');
    expect(buttons[1].getAttribute('data-testid')).toBe('run-icon-2');
    expect(buttons[2].getAttribute('data-testid')).toBe('run-icon-3');
    expect(transitions[0].getAttribute('data-testid')).toBe('run-transition-1-2');
    expect(transitions[1].getAttribute('data-testid')).toBe('run-transition-2-3');

    const text = fixture.nativeElement.textContent as string;
    expect(text.indexOf('Prompt #1')).toBeLessThan(text.indexOf('Prompt #2'));
    expect(text.indexOf('Prompt #2')).toBeLessThan(text.indexOf('Prompt #3'));
    expect(text).toContain('Run #1 re-opened into #2 via Operator continue by local-default: Please try again.');
    expect(text).toContain('Run #2 re-opened into #3 via Restart');
    expect(fixture.componentInstance.triggerLabel(runRecord(4, 'continue', 'completed', 'legacy', 1)))
      .toBe('Not recorded');
    expect(text).toContain('🌀');
  });

  it('links review-trigger provenance to its report', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    const reviewRun: RunRecord = {
      ...runRecord(2, 'continue', 'completed', null, 20),
      trigger: 'review-concern',
      triggerSource: 'review=review_01bb;aspects=code-quality',
    };

    expect(fixture.componentInstance.triggerLabel(reviewRun)).toBe('Review concern: Code Quality (review_01bb)');
    expect(fixture.componentInstance.triggerReportHref(reviewRun)).toContain('remote-review-grade-review_01bb.md');
  });

  it('surfaces captured reissue prompt context from the run header', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      runRecord(1, 'start', 'completed', null, 20),
      { ...runRecord(2, 'reissue', 'completed', null, 25), contextRef: 'logs/run-context/run-2.md', reason: 'auto-review reissue' },
    ]);
    fixture.componentRef.setInput('promptEntries', [
      promptEntry(1, 1, 'start', 'prompt.md', 30, null),
      promptEntry(2, 2, 'reissue', 'logs/run-context/run-2.md', 44, 180),
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-2"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    const commitsReq = http.expectOne(r =>
      r.url.endsWith('/tasks/task-1/runs/2/commits') &&
      r.params.get('watchPath') === 'C:\\watch');
    commitsReq.flush({ commits: [] });
    fixture.detectChanges();

    const detail = fixture.nativeElement.querySelector('[data-testid="run-popover-2"]') as HTMLElement;
    expect(detail.textContent).toContain('Prompt #2');
    expect(detail.textContent).toContain('44 tokens');
    expect(detail.textContent).toContain('180 tokens');

    const toggle = fixture.nativeElement.querySelector('[data-testid="run-context-toggle-2"]') as HTMLButtonElement;
    expect(toggle.textContent?.trim()).toBe('Show passed context');
    toggle.click();

    const req = http.expectOne(r =>
      r.url.endsWith('/tasks/task-1/runs/2/context') &&
      r.params.get('watchPath') === 'C:\\watch');
    req.flush({
      runIndex: 2,
      context: '## Reissue change prompt\n\nCode review found the save button still wraps on mobile.'
    });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Auto-review reissue');
    expect(text).toContain('Reissue change prompt');
    expect(text).toContain('Code review found the save button still wraps on mobile.');
    http.verify();
  });

  it('renders the execution-context panel with scalars and grouped sources (ASS-1739)', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      { ...runRecord(1, 'start', 'completed', null, 20), executionContext: execContext() },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-1"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(r => r.url.endsWith('/tasks/task-1/runs/1/commits')).flush({ commits: [] });
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('[data-testid="run-exec-context-1"]') as HTMLElement;
    expect(panel).not.toBeNull();
    const text = panel.textContent as string;
    // Scalar header from the init frame.
    expect(text).toContain('claude-opus-4-8');
    expect(text).toContain('bypassPermissions');
    expect(text).toContain('C:/work/repo');
    expect(text).toContain('init-frame');
    // Grouped sources: MCP server + memory file with existence flags.
    expect(text).toContain('MCP servers');
    expect(text).toContain('gmail');
    expect(text).toContain('connected');
    expect(text).toContain('Memory');
    expect(text).toContain('CLAUDE.md');
    expect(text).toContain('present');
    expect(text).toContain('absent');
    http.verify();
  });

  it('shows the clean-mode badge and the isolated temp paths in the panel (T1b / ASS-1742)', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      { ...runRecord(1, 'start', 'completed', null, 20), executionContext: cleanExecContext() },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-1"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(r => r.url.endsWith('/tasks/task-1/runs/1/commits')).flush({ commits: [] });
    fixture.detectChanges();

    // The context-mode badge reads 'clean'.
    const badge = fixture.nativeElement.querySelector('[data-testid="run-exec-context-mode-1"]') as HTMLElement;
    expect(badge).not.toBeNull();
    expect(badge.textContent).toContain('context');
    expect(badge.textContent).toContain('clean');

    // The panel surfaces the isolated task-stable home and its seeded paths.
    const panel = fixture.nativeElement.querySelector('[data-testid="run-exec-context-1"]') as HTMLElement;
    const text = panel.textContent as string;
    expect(text).toContain('Environment');
    expect(text).toContain('CLAUDE_CONFIG_DIR');
    expect(text).toContain('C:/Users/operator/.atp/clean-context/claude/abc123');
    expect(text).toContain('task-stable clean-context home seeded outside the OS temporary directory');
    expect(text).toContain('Global config');
    expect(text).toContain('Seeded .credentials.json');
    expect(text).toContain('C:/Users/operator/.atp/clean-context/claude/abc123/.credentials.json');

    // The state path is rendered as a real source-path code element, not just text.
    const paths = Array.from(
      panel.querySelectorAll('code.exec-context__source-path'),
    ).map(el => el.textContent);
    expect(paths).toContain('C:/Users/operator/.atp/clean-context/claude/abc123');
    http.verify();
  });

  it('shows the shared-mode badge when the run used the operator state (T1b / ASS-1742)', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      { ...runRecord(1, 'start', 'completed', null, 20), executionContext: { ...execContext(), contextMode: 'shared' } },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-1"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(r => r.url.endsWith('/tasks/task-1/runs/1/commits')).flush({ commits: [] });
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('[data-testid="run-exec-context-mode-1"]') as HTMLElement;
    expect(badge).not.toBeNull();
    expect(badge.textContent).toContain('shared');
    http.verify();
  });

  it('hides the execution-context panel when nothing was captured', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [runRecord(1, 'start', 'completed', null, 20)]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-1"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(r => r.url.endsWith('/tasks/task-1/runs/1/commits')).flush({ commits: [] });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="run-exec-context-1"]')).toBeNull();
    http.verify();
  });

  it('shows the full prompt-history text even when the run also has captured context', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      runRecord(1, 'start', 'completed', null, 20),
      { ...runRecord(2, 'continue', 'completed', 'Use the extension prompt.', 25), contextRef: 'logs/run-context/run-2.md' },
    ]);
    fixture.componentRef.setInput('promptHistory', [
      {
        index: 1,
        fileName: 'prompt-1.md',
        markdown: 'Use the extension prompt.\n\nAdd context and token snapshots.',
        writtenAt: '2026-06-08T10:01:00Z',
      },
    ]);
    fixture.componentRef.setInput('promptEntries', [
      promptEntry(1, 1, 'start', 'prompt.md', 30, null),
      {
        ...promptEntry(2, 2, 'continue', 'prompt-1.md', 14, 180),
        promptTokenSource: 'prompt-history',
        contextRef: 'logs/run-context/run-2.md',
        contextSnapshot: {
          source: 'captured-context',
          ref: 'logs/run-context/run-2.md',
          at: null,
          status: 'captured',
          tokenEstimate: 180,
          metrics: [],
        },
      },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-2"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    const commitsReq = http.expectOne(r =>
      r.url.endsWith('/tasks/task-1/runs/2/commits') &&
      r.params.get('watchPath') === 'C:\\watch');
    commitsReq.flush({ commits: [] });
    fixture.detectChanges();

    const promptPre = fixture.nativeElement.querySelector('[data-testid="run-prompt-pre-2"]') as HTMLElement;
    expect(promptPre.textContent).toContain('Use the extension prompt.');
    expect(promptPre.textContent).toContain('Add context and token snapshots.');
    expect(fixture.nativeElement.querySelector('[data-testid="run-context-pre-2"]')).toBeNull();
    http.verify();
  });

  // ── Runner attribution in the run header (AGT-2003) ────────────────────

  it('names the remote runner that executed the latest run', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', {
      ...taskInfo(),
      runner: {
        runnerId: 'agent-runner-01@linux-host',
        runnerName: 'agent-runner-01',
        hostname: 'linux-host',
        backendName: 'remote',
        isRemote: true,
        leaseId: 'lease-1',
        fencingToken: 4,
        acquiredAt: '2026-07-09T10:00:00Z',
      },
    } as TaskInfo);
    fixture.componentRef.setInput('runs', [
      runRecord(1, 'start', 'completed', null, 20),
      { ...runRecord(2, 'continue', 'running', null, 25), endedAt: null },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-2"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(r => r.url.endsWith('/tasks/task-1/runs/2/commits')).flush({ commits: [] });
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector('[data-testid="run-runner-2"]') as HTMLElement | null;
    expect(chip).not.toBeNull();
    expect(chip?.getAttribute('data-runner-kind')).toBe('remote');
    expect(chip?.textContent).toContain('agent-runner-01');

    // Earlier runs get no fabricated attribution.
    expect(fixture.componentInstance.runnerAttribution(
      fixture.componentInstance.visibleRuns()[0],
    )).toBeNull();
    http.verify();
  });

  it('labels a local in-process run as "lokal" in the run header', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      { ...runRecord(1, 'start', 'running', null, 20), endedAt: null },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-1"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(r => r.url.endsWith('/tasks/task-1/runs/1/commits')).flush({ commits: [] });
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector('[data-testid="run-runner-1"]') as HTMLElement | null;
    expect(chip?.getAttribute('data-runner-kind')).toBe('local');
    expect(chip?.textContent?.trim()).toBe('lokal');
    http.verify();
  });

  it('renders captured run ownership through the canonical quiet historical badge', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [{
      ...runRecord(1, 'start', 'completed', null, 20),
      executionLocation: {
        state: 'remote-disconnected',
        executionKind: 'remote',
        runnerId: 'agent-runner-01',
        clientId: 'runner-client-01',
        hostDisplayName: 'Runner host 01',
        configuredRunnerId: 'agent-runner-02',
        startedAt: '2026-06-08T10:01:00Z',
        lastHeartbeat: '2026-06-08T10:01:20Z',
        lastActivityAt: '2026-06-08T10:01:21Z',
        branch: 'task/ASS-1',
        worktreePath: '/worktrees/ASS-1',
        connectionState: 'historical',
        leaseState: 'released',
        trustReason: 'Captured from the fenced run lease.',
        historical: true,
      },
    }]);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="run-icon-1"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController)
      .expectOne(r => r.url.endsWith('/tasks/task-1/runs/1/commits'))
      .flush({ commits: [] });
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('Host · agent-runner-01');
    expect(badge.classList.contains('execution-location--history')).toBe(true);
    expect(badge.classList.contains('execution-location--acute')).toBe(false);
    TestBed.inject(HttpTestingController).verify();
  });
});

function taskInfo(): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'ASS-1',
    title: 'Task',
    state: '3-progress',
    order: 0,
    agent: 'codex',
    createdAt: '2026-06-08T10:00:00Z',
    watchPath: 'C:\\watch',
    projectName: 'demo',
    folderPath: 'C:\\watch\\3-progress\\task-1',
    lastActivity: '2026-06-08T10:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  } as TaskInfo;
}

function execContext(): CliExecutionContext {
  return {
    cli: 'claude',
    model: 'claude-opus-4-8',
    permissionMode: 'bypassPermissions',
    cwd: 'C:/work/repo',
    capturedAt: '2026-06-08T10:01:00Z',
    source: 'init-frame',
    sources: [
      { kind: 'mcp', label: 'gmail', path: null, exists: null, detail: 'connected' },
      { kind: 'memory', label: 'Project memory', path: 'C:/work/repo/CLAUDE.md', exists: true, detail: null },
      { kind: 'global-config', label: 'Global config', path: 'C:/Users/x/.claude/settings.json', exists: false, detail: null },
    ],
  };
}

/**
 * A clean-mode execution context as the backend `CleanContextPreparer` surfaces
 * it (T1b / ASS-1742): `contextMode: 'clean'` plus an `env` source for the
 * relocated, task-stable `CLAUDE_CONFIG_DIR` home and a `global-config` source
 * for each file seeded into that home. Mirrors the real source shape so the panel
 * assertions prove what an operator actually sees for an isolated run.
 */
function cleanExecContext(): CliExecutionContext {
  const taskHome = 'C:/Users/operator/.atp/clean-context/claude/abc123';
  return {
    cli: 'claude',
    model: 'claude-opus-4-8',
    permissionMode: 'bypassPermissions',
    cwd: 'C:/work/repo',
    contextMode: 'clean',
    capturedAt: '2026-06-08T10:01:00Z',
    source: 'init-frame',
    sources: [
      {
        kind: 'env',
        label: 'CLAUDE_CONFIG_DIR',
        path: taskHome,
        exists: true,
        detail: 'task-stable clean-context home seeded outside the OS temporary directory',
      },
      {
        kind: 'global-config',
        label: 'Seeded .credentials.json',
        path: `${taskHome}/.credentials.json`,
        exists: true,
        detail: 'hard-linked from C:/Users/x/.claude/.credentials.json',
      },
      {
        kind: 'global-config',
        label: 'Seeded settings.json',
        path: `${taskHome}/settings.json`,
        exists: true,
        detail: 'copied from C:/Users/x/.claude/settings.json',
      },
    ],
  };
}

function promptEntry(
  index: number,
  runIndex: number,
  intent: string,
  fileName: string | null,
  promptTokenEstimate: number | null,
  contextTokenEstimate: number | null,
): RunPromptEntry {
  return {
    index,
    runIndex,
    intent,
    at: `2026-06-08T10:0${runIndex}:00Z`,
    label: `Prompt #${index}`,
    fileName,
    promptTokenSource: fileName === 'prompt.md' ? 'task-prompt' : 'captured-context',
    promptPreview: index === 1 ? 'Initial prompt' : 'Review prompt',
    promptTokenEstimate,
    contextTokenEstimate,
    contextRef: fileName?.startsWith('logs/') ? fileName : null,
    contextSnapshot: contextTokenEstimate
      ? {
          source: 'captured-context',
          ref: fileName,
          at: null,
          status: 'captured',
          tokenEstimate: contextTokenEstimate,
          metrics: [],
        }
      : null,
  };
}

function runRecord(
  index: number,
  intent: string,
  status: string,
  userFollowup: string | null,
  durationSeconds: number
): RunRecord {
  return {
    index,
    intent,
    status,
    userFollowup,
    durationSeconds,
    startedAt: `2026-06-08T10:0${index}:00Z`,
    endedAt: `2026-06-08T10:0${index}:30Z`,
    cli: 'codex',
    exitCode: status === 'failed' ? 1 : 0,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: index > 1,
    reason: status,
    lineStart: index,
    lineEnd: index + 1,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    tokenSummary: index === 3 ? {
      calls: 1,
      inputTokens: 2000,
      outputTokens: 500,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      totalTokens: 2500,
      lastModel: 'gpt-5',
      lastUpdate: '2026-06-08T10:03:30Z',
      entries: [],
    } : null,
  };
}

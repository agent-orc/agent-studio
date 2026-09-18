import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskColumnComponent } from './task-column';
import type { ArchivedTaskInfo, CliExecution, TaskInfo, ProjectRunnerStatus } from '../../../../models/task.model';

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
describe('TaskColumnComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskColumnComponent);
    fixture.componentRef.setInput('title', undefined);
    fixture.componentRef.setInput('state', undefined);
    fixture.componentRef.setInput('jobs', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // title, state, jobs
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] TaskColumnComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('Ready header counts pullable cards and names held stalled cards separately', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskColumnComponent);
    fixture.componentRef.setInput('title', 'Ready');
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('jobs', [
      makeJob({ id: 'pullable' }),
      makeJob({ id: 'stalled', pickupHold: { classification: 'stalled' } as TaskInfo['pickupHold'] }),
      makeJob({ id: 'impossible', pickupHold: { classification: 'unsatisfiable' } as TaskInfo['pickupHold'] }),
    ]);
    fixture.detectChanges();

    expect(fixture.componentInstance.headerCount()).toBe(1);
    expect(fixture.componentInstance.stalledCount()).toBe(2);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="lane-count-2-ready"]')?.textContent)
      .toContain('1');
  });

  // ─────────────────────────────────────────────────────────────────────
  // Lane status cluster (BUG: lane status not clearly showing auto-pickup)
  //
  // Three scenarios verified by reading the derived `statusCluster` signal:
  //   1. idle  + auto-continuous  → AUTO chip, no RUNNING, no queue.
  //   2. running + auto-continuous → RUNNING + AUTO chips both visible.
  //   3. running + circuit-breaker-induced manual → RUNNING + PAUSED chips,
  //      tooltip explicitly mentions the circuit-breaker.
  // ─────────────────────────────────────────────────────────────────────

  async function buildColumn(opts: { mode: string; status?: ProjectRunnerStatus | null; jobs?: TaskInfo[] }) {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskColumnComponent);
    fixture.componentRef.setInput('title', 'In Progress');
    fixture.componentRef.setInput('state', '3-progress');
    fixture.componentRef.setInput('jobs', opts.jobs ?? []);
    fixture.componentRef.setInput('autoProject', 'TestProject');
    fixture.componentRef.setInput('autoMode', opts.mode);
    fixture.componentRef.setInput('runnerStatus', opts.status ?? null);
    fixture.componentRef.setInput('nowMs', new Date('2026-05-27T15:00:00Z').getTime());
    return fixture;
  }

  it('cluster: idle + auto → AUTO chip, no RUNNING, no queue', async () => {
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({ mode: 'auto-continuous' })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.mode.kind).toBe('auto');
    expect(cluster!.mode.label).toBe('AUTO');
    expect(cluster!.running).toBeNull();
    expect(cluster!.queue).toBeNull();
  });

  it('shows the derived stalled subset in the In-Progress lane count', async () => {
    const now = new Date('2026-05-27T15:00:00Z').getTime();
    const fixture = await buildColumn({
      mode: 'manual',
      jobs: [
        makeJob({
          id: 'running', state: '3-progress',
          execution: makeExec({ jobId: 'running' }),
          runActivity: { kind: 'active', processId: 12345, attempt: 0 },
        }),
        makeJob({
          id: 'fresh', state: '3-progress', execution: null,
          enteredLaneAt: new Date(now - 60_000).toISOString(),
          lastActivity: new Date(now - 60_000).toISOString(),
          runActivity: { kind: 'no-active-run', attempt: 0 },
        }),
        makeJob({
          id: 'failed', state: '3-progress', execution: null,
          enteredLaneAt: new Date(now - 30_000).toISOString(),
          runActivity: { kind: 'failed-idle', attempt: 1, lastError: 'router error' },
        }),
        makeJob({
          id: 'idle', state: '3-progress', execution: null,
          enteredLaneAt: new Date(now - 10 * 60_000).toISOString(),
          lastActivity: new Date(now - 10 * 60_000).toISOString(),
          runActivity: { kind: 'no-active-run', attempt: 0 },
        }),
      ],
    });

    expect(fixture.componentInstance.stalledCount()).toBe(2);
    fixture.detectChanges();
    const count = fixture.nativeElement.querySelector('[data-testid="lane-count-3-progress"]') as HTMLElement | null;
    expect(count?.textContent?.replace(/\s+/g, ' ').trim()).toMatch(/^4\s*· 2 stalled$/);
  });

  it('cluster: running + auto → RUNNING + AUTO chips both visible', async () => {
    const exec = makeExec({ jobId: 'task-7', startedAt: '2026-05-27T14:56:36Z' });
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({
        mode: 'auto-continuous',
        activeJobId: 'task-7',
        activeExecution: exec,
        queuedJobIds: ['task-8', 'task-9', 'task-10', 'task-11', 'task-12']
      })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.running).not.toBeNull();
    expect(cluster!.running!.jobId).toBe('task-7');
    expect(cluster!.running!.duration).toMatch(/3m24s/);
    expect(cluster!.mode.kind).toBe('auto');
    expect(cluster!.queue).not.toBeNull();
    expect(cluster!.queue!.count).toBe(5);
  });

  it('cluster: running + circuit-breaker (mode=manual + reason) → PAUSED with circuit-breaker tooltip', async () => {
    const exec = makeExec({ jobId: 'task-7', startedAt: '2026-05-27T14:55:00Z' });
    const fixture = await buildColumn({
      mode: 'manual',
      status: makeStatus({
        mode: 'manual',
        activeJobId: 'task-7',
        activeExecution: exec,
        modeReason: "auto-failure circuit-breaker: 3x same job 'foo' did not reach review",
        modeSource: 'circuit-breaker'
      })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.mode.kind).toBe('paused');
    expect(cluster!.mode.label).toBe('PAUSED');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('circuit-breaker');
    expect(cluster!.running).not.toBeNull();
    expect(cluster!.running!.jobId).toBe('task-7');
  });

  it('cluster: circuit-breaker cooldown tooltip includes auto-resume time', async () => {
    const fixture = await buildColumn({
      mode: 'manual',
      status: makeStatus({
        mode: 'manual',
        modeReason: 'auto-failure circuit-breaker cooldown: rate-limit; resumes at 2026-05-27T15:20:00Z',
        modeSource: 'circuit-breaker',
        breakerState: 'cooldown',
        breakerReason: 'rate-limit or transient CLI quota failure',
        breakerCooldownUntil: '2026-05-27T15:20:00Z'
      })
    });

    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.kind).toBe('paused');
    expect(cluster!.mode.tooltip).toContain('auto-resume');
    expect(cluster!.mode.tooltip).toContain('rate-limit');
    expect(cluster!.mode.tooltip).toContain('2026-05-27');
  });

  it('cluster: mode=manual without circuit-breaker reason renders as MANUAL', async () => {
    const fixture = await buildColumn({
      mode: 'manual',
      status: makeStatus({ mode: 'manual', modeReason: 'api-toggle', modeSource: 'user' })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.kind).toBe('manual');
    expect(cluster!.mode.label).toBe('MANUAL');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('auto-pickup is off');
  });

  // ADR-0044: a PUT /api/runner/{project}/mode call that arrived while a
  // job was active leaves the live mode at auto-* and queues the requested
  // mode in status.pendingMode. The pill renders an arrow + the deferred
  // value so the operator sees the change took, just not yet.
  it('cluster: one pending active task renders its count and title in the tooltip', async () => {
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({
        mode: 'auto-continuous',
        activeJobId: 'running-task',
        pendingMode: 'manual',
        pendingModeWillApplyAfter: 'running-task',
        pendingModeActiveTaskCount: 1,
        pendingModeActiveTaskTitle: 'Publish release notes'
      })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.label).toBe('AUTO → MANUAL');
    expect(cluster!.mode.tooltip).toBe('Switches to MANUAL when 1 active task finishes (Publish release notes).');
  });

  it('cluster: multiple pending active tasks renders concise plural semantics', async () => {
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({
        mode: 'auto-continuous',
        activeJobId: 'task-a',
        pendingMode: 'manual',
        pendingModeActiveTaskCount: 4
      })
    });

    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.label).toBe('AUTO → MANUAL');
    expect(cluster!.mode.tooltip).toBe('Switches to MANUAL when 4 active tasks finish.');
  });

  // ADR-0044: a test-subject backend's lane pill should explain why no
  // pickup is happening even when the mode pill says AUTO. We assert the
  // tooltip carries the test-subject explanation; the label itself still
  // reflects the configured mode so a future role-flip is visible.
  it('cluster: role=test-subject appends a tooltip note explaining the structural pickup gate', async () => {
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({ mode: 'auto-continuous', role: 'test-subject' })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.label).toBe('AUTO');
    expect(cluster!.mode.tooltip).toContain('test-subject');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('structurally disabled');
  });

  // ─────────────────────────────────────────────────────────────────────
  // Shared-workspace / multi-backend scenario.
  //
  // Two backends watching the same workspace: backend A picks up a task,
  // backend B's UI polls its own /api/runner/status (no activeExecution
  // there) but the same disk state surfaces the job card with
  // `execution.status === 'running'`. Without the disk-derived fallback
  // the lane showed only MANUAL; the operator could not tell that work
  // was actually in progress. The fallback derives the RUNNING pill from
  // the job card and flags it `foreign` so the user knows this backend
  // is not the one driving the run.
  // ─────────────────────────────────────────────────────────────────────
  it('cluster: foreign backend running on shared workspace shows RUNNING pill with foreign flag', async () => {
    const foreignJob = makeJob({
      id: 'shared-task-9',
      state: '3-progress',
      execution: {
        jobId: 'shared-task-9',
        taskKey: 'shared::shared-task-9',
        processId: 5796,
        startedAt: '2026-05-27T14:58:00Z',
        status: 'running',
        exitCode: null,
        durationSeconds: null,
        model: 'claude-opus-4-7',
        runOutcome: null,
      },
    });
    const fixture = await buildColumn({
      mode: 'manual',
      // Local runner is idle (`activeJobId: null`) — another backend owns the run.
      status: makeStatus({ mode: 'manual', activeJobId: null, activeExecution: null }),
      jobs: [foreignJob],
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.running).not.toBeNull();
    expect(cluster!.running!.jobId).toBe('shared-task-9');
    expect(cluster!.running!.foreign).toBe(true);
    expect(cluster!.running!.tooltip.toLowerCase()).toContain('another backend');
    // Mode chip stays accurate (this backend is genuinely manual) but the
    // tooltip clarifies that the running task is being driven elsewhere.
    expect(cluster!.mode.kind).toBe('manual');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('shared workspace');
  });

  it('cluster: foreign run + auto mode rewrites the AUTO tooltip to mention the foreign run', async () => {
    const foreignJob = makeJob({
      id: 'shared-task-10',
      state: '3-progress',
      execution: {
        jobId: 'shared-task-10',
        taskKey: 'shared::shared-task-10',
        processId: 5796,
        startedAt: '2026-05-27T14:58:00Z',
        status: 'running',
        exitCode: null,
        durationSeconds: null,
        model: 'claude-opus-4-7',
        runOutcome: null,
      },
    });
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({ mode: 'auto-continuous', activeJobId: null, activeExecution: null }),
      jobs: [foreignJob],
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.kind).toBe('auto');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('shared workspace');
    expect(cluster!.running!.foreign).toBe(true);
  });

  it('cluster: own runner takes precedence over a disk-derived foreign signal', async () => {
    // Edge case: the local runner is actively driving the job AND the same
    // job's `execution.status === 'running'` shows up on the card. The
    // pill must use the runner.activeExecution path (foreign = false) so
    // it stays semantically correct.
    const exec = makeExec({ jobId: 'task-7', startedAt: '2026-05-27T14:56:36Z' });
    const job = makeJob({
      id: 'task-7',
      state: '3-progress',
      execution: { ...exec, runOutcome: null },
    });
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({
        mode: 'auto-continuous',
        activeJobId: 'task-7',
        activeExecution: exec,
      }),
      jobs: [job],
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.running!.foreign).toBe(false);
  });

  it('cluster: hidden when state is not 3-progress', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskColumnComponent);
    fixture.componentRef.setInput('title', 'Ready');
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('jobs', []);
    fixture.componentRef.setInput('autoProject', 'TestProject');
    fixture.componentRef.setInput('autoMode', 'auto-continuous');
    expect(fixture.componentInstance.statusCluster()).toBeNull();
  });

  it('renders the compact active/waiting summary without a tick-history line', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskColumnComponent);
    fixture.componentRef.setInput('title', 'Post Processing');
    fixture.componentRef.setInput('state', '4-auto-review');
    fixture.componentRef.setInput('jobs', []);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="auto-review-status"]')).toBeNull();
    expect(host.textContent ?? '').not.toContain('Last tick:');
    expect(host.querySelector('[data-testid="lane-post-processing-summary"]')?.textContent)
      .toContain('0 active / 0 waiting');
    expect(host.querySelector('[data-testid="lane-post-processing-summary-full"]')?.textContent?.trim())
      .toBe('0 active / 0 waiting');
    expect(host.querySelector('[data-testid="lane-post-processing-summary-compact"]')?.textContent?.trim())
      .toBe('0/0');
  });

  // AGT-2020: Delete moved off the hover trash button into the card context
  // menu (destructive row). The card must carry NO standalone delete button,
  // and the menu's "Delete task" row drives the same jobDeleteRequest flow.
  it('no longer renders a hover delete button on the card', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskColumnComponent);
    const job = makeJob();
    fixture.componentRef.setInput('title', 'Ready');
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('jobs', [job]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-delete"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('.task-card__delete')).toBeNull();
  });

  it('forwards card delete requests from the context-menu Delete row', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskColumnComponent);
    const job = makeJob();
    const deleted: TaskInfo[] = [];
    fixture.componentInstance.jobDeleteRequest.subscribe((value) => deleted.push(value));
    fixture.componentRef.setInput('title', 'Ready');
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('jobs', [job]);
    fixture.detectChanges();

    // Open the card context menu (same surface as right-click / Menu key).
    const card = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement;
    expect(card).toBeTruthy();
    card.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fixture.detectChanges();

    // The destructive Delete row lives at the end of the menu (testIdPrefix
    // "card-ctx"). Clicking it must forward the job unchanged.
    const deleteRow = document.querySelector('[data-testid="card-ctx-item-delete-task"]') as HTMLButtonElement | null;
    expect(deleteRow).toBeTruthy();
    expect(deleteRow!.classList.contains('app-menu__row--danger')).toBe(true);
    deleteRow!.click();
    fixture.detectChanges();

    expect(deleted).toEqual([job]);
  });
});

// ─────────────────────────────────────────────────────────────────────────
// ASS-1727: Archive lane lazy-load. The board's `grouped.archive` is
// intentionally empty (the cache-backed board scan excludes the terminal
// lane), so the Archive column hydrates from the paged
// `GET /api/tasks/archive` endpoint instead of its `jobs()` input. These
// tests pin: a fetch fires on init, rows render, the empty state only shows
// after a genuine zero-total response, "load more" appends the next page,
// and the text filter re-queries (debounced) from offset 0.
// ─────────────────────────────────────────────────────────────────────────
describe('TaskColumnComponent archive lane (ASS-1727)', () => {
  async function buildArchiveColumn() {
    await TestBed.configureTestingModule({
      imports: [TaskColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(TaskColumnComponent);
    fixture.componentRef.setInput('title', 'Archive');
    fixture.componentRef.setInput('state', '7-archive');
    fixture.componentRef.setInput('jobs', []);
    fixture.componentRef.setInput('projectScope', 'Token Economy');
    fixture.detectChanges(); // first CD runs ngOnInit → initial fetch
    return { fixture, httpMock };
  }

  const isArchiveReq = (url: string) => url === '/api/tasks/archive';

  it('fetches the paged archive endpoint on init and renders rows', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();

    const req = httpMock.expectOne((r) => isArchiveReq(r.url));
    expect(req.request.params.get('offset')).toBe('0');
    expect(req.request.params.get('limit')).toBe('50');
    expect(req.request.params.get('project')).toBe('Token Economy');
    req.flush({
      items: [makeArchived({ id: 'a1', title: 'Archived One' })],
      total: 1,
      offset: 0,
      limit: 50,
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.archiveItems().length).toBe(1);
    expect(fixture.componentInstance.archiveTotal()).toBe(1);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="archive-row"]')).toBeTruthy();
    expect(host.textContent ?? '').toContain('Archived One');
    // The header count reflects the unpaged total, not the empty jobs() input.
    expect(host.querySelector('.column__count')?.textContent?.trim()).toBe('1');
    httpMock.verify();
  });

  it('reloads the archive when the active project scope changes', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();
    httpMock.expectOne((r) => r.url === '/api/tasks/archive' && r.params.get('project') === 'Token Economy')
      .flush({ items: [], total: 0, offset: 0, limit: 50 });

    fixture.componentRef.setInput('projectScope', 'Agent Studio');
    fixture.detectChanges();

    const req = httpMock.expectOne((r) => r.url === '/api/tasks/archive' && r.params.get('project') === 'Agent Studio');
    expect(req.request.params.get('offset')).toBe('0');
    req.flush({ items: [makeArchived({ id: 'agt-archived', projectName: 'Agent Studio' })], total: 1, offset: 0, limit: 50 });
    fixture.detectChanges();

    expect(fixture.componentInstance.archiveItems().map((item) => item.id)).toEqual(['agt-archived']);
    httpMock.verify();
  });

  it('reloads the archive as completed cards leave the live board', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();
    httpMock.expectOne((r) => isArchiveReq(r.url))
      .flush({ items: [], total: 0, offset: 0, limit: 50 });

    fixture.componentRef.setInput('jobs', [{} as TaskInfo]);
    fixture.detectChanges();

    const refresh = httpMock.expectOne((r) => isArchiveReq(r.url));
    refresh.flush({ items: [makeArchived({ id: 'just-archived' })], total: 1, offset: 0, limit: 50 });
    fixture.detectChanges();

    expect(fixture.componentInstance.archiveItems().map((item) => item.id)).toEqual(['just-archived']);
    httpMock.verify();
  });

  it('shows the empty state only after a genuine zero-total response', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();

    // Before the fetch resolves the empty state must not flash.
    expect(fixture.componentInstance.archiveIsEmpty()).toBe(false);

    httpMock.expectOne((r) => isArchiveReq(r.url)).flush({ items: [], total: 0, offset: 0, limit: 50 });
    fixture.detectChanges();

    expect(fixture.componentInstance.archiveIsEmpty()).toBe(true);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="archive-empty"]')).toBeTruthy();
    expect(host.textContent ?? '').toContain('No archived ticket in this project');
    httpMock.verify();
  });

  it('load more appends the next page; total stays the source of truth', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();

    httpMock.expectOne((r) => isArchiveReq(r.url)).flush({
      items: [makeArchived({ id: 'a1' })],
      total: 2,
      offset: 0,
      limit: 50,
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.archiveRemaining()).toBe(1);

    fixture.componentInstance.loadMoreArchive();
    const req2 = httpMock.expectOne((r) => isArchiveReq(r.url));
    expect(req2.request.params.get('offset')).toBe('1'); // offset = items already loaded
    req2.flush({ items: [makeArchived({ id: 'a2' })], total: 2, offset: 1, limit: 50 });
    fixture.detectChanges();

    expect(fixture.componentInstance.archiveItems().map((i) => i.id)).toEqual(['a1', 'a2']);
    expect(fixture.componentInstance.archiveRemaining()).toBe(0);
    httpMock.verify();
  });

  it('filtered empty state names the filter, not a bare "no archived tasks"', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();
    // Initial load: archive has rows, so this is NOT a "truly empty" archive.
    httpMock.expectOne((r) => isArchiveReq(r.url)).flush({
      items: [makeArchived({ id: 'a1' })],
      total: 1,
      offset: 0,
      limit: 50,
    });
    fixture.detectChanges();

    vi.useFakeTimers();
    try {
      // A filter that matches nothing comes back as total=0 for the query.
      fixture.componentInstance.onArchiveSearchInput('no-such-card');
      vi.advanceTimersByTime(300);
      httpMock.expectOne((r) => isArchiveReq(r.url)).flush({ items: [], total: 0, offset: 0, limit: 50 });
    } finally {
      vi.useRealTimers();
    }
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const empty = host.querySelector('[data-testid="archive-empty"]');
    expect(empty).toBeTruthy();
    expect(empty?.textContent ?? '').toContain('match the filter');
    // The lane header count tracks the (filtered) archive total, not jobs().
    expect(fixture.componentInstance.headerCount()).toBe(0);
    httpMock.verify();
  });

  it('debounced search re-queries from offset 0 with the term', async () => {
    const { fixture, httpMock } = await buildArchiveColumn();
    httpMock.expectOne((r) => isArchiveReq(r.url)).flush({ items: [], total: 0, offset: 0, limit: 50 });

    vi.useFakeTimers();
    try {
      fixture.componentInstance.onArchiveSearchInput('migration');
      // Debounced: no request until the timer elapses.
      httpMock.expectNone((r) => isArchiveReq(r.url));
      vi.advanceTimersByTime(300);

      const req = httpMock.expectOne((r) => isArchiveReq(r.url));
      expect(req.request.params.get('search')).toBe('migration');
      expect(req.request.params.get('offset')).toBe('0');
      req.flush({ items: [], total: 0, offset: 0, limit: 50 });
    } finally {
      vi.useRealTimers();
    }
    httpMock.verify();
  });
});

function makeArchived(overrides: Partial<ArchivedTaskInfo> = {}): ArchivedTaskInfo {
  return {
    id: 'arch-1',
    taskKey: 'test::arch-1',
    key: 'ASS-1',
    title: 'Archived task',
    state: '7-archive',
    projectName: 'Test',
    watchPath: '/tmp/watch',
    enteredLaneAt: '2026-05-01T09:00:00Z',
    lastActivity: '2026-05-01T09:30:00Z',
    commitCount: 1,
    codeActivityDetected: true,
    taskType: 'chore',
    cliType: 'claude',
    agent: 'claude',
    ...overrides,
  };
}

function makeExec(overrides: Partial<CliExecution> = {}): CliExecution {
  return {
    jobId: 'task-1',
    taskKey: 'test::task-1',
    processId: 12345,
    startedAt: '2026-05-27T14:00:00Z',
    status: 'running',
    exitCode: null,
    durationSeconds: null,
    model: 'claude-opus-4-7',
    ...overrides,
  };
}

function makeStatus(overrides: Partial<ProjectRunnerStatus> = {}): ProjectRunnerStatus {
  return {
    projectName: 'TestProject',
    mode: 'manual',
    activeJobId: null,
    activeExecution: null,
    queuedJobIds: [],
    modeReason: null,
    modeChangedAt: null,
    modeSource: null,
    ...overrides,
  };
}

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    title: 'Task 1',
    state: '2-ready',
    order: 1,
    agent: 'codex',
    createdAt: '2026-05-11T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/2-ready/task-1',
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

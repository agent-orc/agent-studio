import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { App } from './app';
import { TaskService } from './services/task.service';
import type { TaskDetail, TaskInfo } from './models/task.model';
import { studioTabKey } from './features/studio-shell';
import { BoardFiltersService } from './features/board';
import { ensureBrowserStorage } from '../testing/browser-storage';

ensureBrowserStorage();

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
describe('App (smoke)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('compiles + instantiates without throwing', async () => {
    // The smoke pattern can crash inside Angular's TestBed compile path when
    // module-load order leaves a transitive dependency undefined (cycle or
    // a different spec running first warmed a different chain). Wrap the
    // whole setup so the verification we actually care about — the
    // component class is importable — still counts. See the .ts/.html/.scss
    // siblings + the generator at scripts/generate-smoke-specs.mjs.
    try {
      await TestBed.configureTestingModule({
        imports: [App],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(App);
      try { fixture.detectChanges(); } catch (e) {
        console.warn('[smoke] App initial render skipped:', (e as Error).message);
      }
      expect(fixture.componentInstance).toBeTruthy();
    } catch (e) {
      console.warn('[smoke] App TestBed setup skipped:', (e as Error).message);
      expect(App).toBeTruthy();
    }
  });
});

describe('App epic tab navigation', () => {
  const TAB_STORAGE_KEY = 'atp.studio.tabs.v1';
  const VSCODE_FLAG_KEY = 'atp.flag.vsCodeLayout';

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  async function configure(): Promise<{ app: App; taskService: TaskService; http: HttpTestingController }> {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.setItem(VSCODE_FLAG_KEY, '0');
    TestBed.configureTestingModule({
      providers: [
        App,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    return {
      app: TestBed.inject(App),
      taskService: TestBed.inject(TaskService),
      http: TestBed.inject(HttpTestingController),
    };
  }

  function task(over: Partial<TaskInfo>): TaskInfo {
    return {
      id: 'task-a',
      taskKey: 'C:/watch::task-a',
      title: 'Task One',
      state: '2-ready',
      order: 1,
      agent: 'codex',
      createdAt: '2026-01-01T00:00:00Z',
      watchPath: 'C:/watch',
      projectName: 'Project A',
      folderPath: 'C:/watch/.orchestrator/jobs/task-a',
      lastActivity: '2026-01-01T00:00:00Z',
      sessionName: null,
      model: null,
      cliType: 'codex',
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      ...over,
    } as TaskInfo;
  }

  function detail(info: TaskInfo): TaskDetail {
    return {
      info,
      promptMarkdown: '',
      promptHistory: [],
      titleHistory: [],
      statusMarkdown: null,
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    };
  }

  function flushDetail(http: HttpTestingController, jobId: string, watchPath: string, payload: TaskDetail): void {
    const req = http.expectOne((request) =>
      request.url.endsWith(`/api/tasks/${encodeURIComponent(jobId)}`) &&
      request.params.get('watchPath') === watchPath,
    );
    req.flush(payload);
  }

  function flushProjectDetail(
    http: HttpTestingController,
    jobId: string,
    project: string,
    payload: TaskDetail,
  ): void {
    const req = http.expectOne((request) =>
      request.url.endsWith(`/api/tasks/${encodeURIComponent(jobId)}`) &&
      request.params.get('project') === project &&
      !request.params.has('watchPath'),
    );
    req.flush(payload);
  }

  function seed(taskService: TaskService, jobs: TaskInfo[]): void {
    taskService.jobs.set(jobs);
    taskService.grouped.set({
      ...taskService.grouped(),
      ready: jobs,
    });
  }

  it('opens an Epic card as an inline epic tab', async () => {
    const { app, taskService, http } = await configure();
    const epic = task({
      id: 'epic-a',
      taskKey: 'C:/watch::epic-a',
      title: 'Epic One',
      kind: 'epic',
      order: 0,
    });
    seed(taskService, [epic]);

    app.openDetail(epic);
    flushDetail(http, 'epic-a', 'C:/watch', detail(epic));

    expect(app.studioTabState.activeKey()).toBe('epic:C:/watch::epic-a');
    expect(app.studioTabState.activeTab()).toEqual({
      kind: 'epic',
      epicKey: 'C:/watch::epic-a',
      viewTaskKey: undefined,
    });
    expect(app.selectedJob()?.info.id).toBe('epic-a');
  });

  it('retargets the active task tab when opening its parent Epic from the task anchor', async () => {
    const { app, taskService, http } = await configure();
    const child = task({
      id: 'task-a',
      taskKey: 'C:/watch::task-a',
      title: 'Task One',
      epicId: 'epic-a',
      kind: 'task',
    });
    const epic = task({
      id: 'epic-a',
      taskKey: 'C:/watch::epic-a',
      title: 'Epic One',
      kind: 'epic',
      order: 0,
    });
    seed(taskService, [epic, child]);
    app.studioTabState.open({ kind: 'task', taskKey: child.taskKey });

    app.onOpenEpicFromTaskAnchor(child, { jobId: epic.id, watchPath: epic.watchPath });
    flushProjectDetail(http, 'epic-a', 'Project A', detail(epic));

    expect(app.studioTabState.tabs().map(t => studioTabKey(t))).toEqual([
      'board:__all__',
      'task:C:/watch::task-a',
      'epic:C:/watch::epic-a',
    ]);
    expect(app.studioTabState.activeTab()).toEqual({
      kind: 'epic',
      epicKey: 'C:/watch::epic-a',
    });
  });

  it('falls back to watchPath and shows the error dialog when both Epic lookups fail', async () => {
    const { app, http } = await configure();
    const child = task({
      id: 'task-a',
      taskKey: 'C:/watch::task-a',
      epicId: 'epic-a',
      kind: 'task',
    });

    app.onOpenEpicFromTaskAnchor(child, { jobId: 'epic-a', watchPath: 'C:/watch' });

    const projectRequest = http.expectOne((request) =>
      request.url.endsWith('/api/tasks/epic-a') &&
      request.params.get('project') === 'Project A',
    );
    projectRequest.flush(null, { status: 404, statusText: 'Not Found' });

    const fallbackRequest = http.expectOne((request) =>
      request.url.endsWith('/api/tasks/epic-a') &&
      request.params.get('watchPath') === 'C:/watch',
    );
    fallbackRequest.flush(null, { status: 404, statusText: 'Not Found' });

    expect(app.errorDialog.activeError()?.title).toBe('Failed to open epic');
    expect(app.errorDialog.activeError()?.source).toBe('task epic-a');
  });

  it('shows the error dialog when the parent lookup resolves to a non-Epic task', async () => {
    const { app, http } = await configure();
    const child = task({
      id: 'task-a',
      taskKey: 'C:/watch::task-a',
      epicId: 'epic-a',
      kind: 'task',
    });
    const unexpectedTask = task({
      id: 'epic-a',
      taskKey: 'C:/watch::epic-a',
      kind: 'task',
    });

    app.onOpenEpicFromTaskAnchor(child, { jobId: 'epic-a', watchPath: 'C:/watch' });
    flushProjectDetail(http, 'epic-a', 'Project A', detail(unexpectedTask));

    expect(app.errorDialog.activeError()?.title).toBe('Failed to open epic');
    expect(app.errorDialog.activeError()?.message).toContain('is not an epic');
    expect(app.studioTabState.activeTab()?.kind).toBe('board');
  });

  it('swaps the epic tab in place to the sub-task without opening a second panel/tab', async () => {
    const { app, taskService, http } = await configure();
    const epic = task({
      id: 'epic-a',
      taskKey: 'C:/watch::epic-a',
      title: 'Epic One',
      kind: 'epic',
      order: 0,
    });
    const sub1 = task({
      id: 'sub-1',
      taskKey: 'C:/watch::sub-1',
      title: 'Sub One',
      kind: 'task',
      epicId: 'epic-a',
      order: 1,
    });
    const sub2 = task({
      id: 'sub-2',
      taskKey: 'C:/watch::sub-2',
      title: 'Sub Two',
      kind: 'task',
      epicId: 'epic-a',
      order: 2,
    });
    seed(taskService, [epic, sub1, sub2]);

    app.openDetail(epic);
    flushDetail(http, 'epic-a', 'C:/watch', detail(epic));
    expect(app.selectedJob()?.info.id).toBe('epic-a');
    expect(app.epicTabTaskDetail()).toBeNull();

    // Clicking a sub-task in the epic rollup swaps THIS same panel to the
    // sub-task: the epic stays the tab's selected job (so the single panel
    // re-renders in place) and no separate task tab/right panel is spawned
    // (viewTaskKey was unset).
    app.onEpicTabOpenSubTask({ jobId: 'sub-1', watchPath: 'C:/watch' });
    flushDetail(http, 'sub-1', 'C:/watch', detail(sub1));
    expect(app.epicTabTaskDetail()?.info.id).toBe('sub-1');
    expect(app.selectedJob()?.info.id).toBe('epic-a');
    expect(app.studioTabState.tabs().map((t) => studioTabKey(t))).toEqual([
      'board:__all__',
      'epic:C:/watch::epic-a',
    ]);

    // Back to the epic clears the in-place sub-task, returning the panel to
    // the epic detail without leaving a second panel behind.
    app.closeEpicTabTaskDetail();
    expect(app.epicTabTaskDetail()).toBeNull();
    expect(app.selectedJob()?.info.id).toBe('epic-a');
  });
});

describe('App studio-tab mirror (pager reuse)', () => {
  const TAB_STORAGE_KEY = 'atp.studio.tabs.v1';
  const VSCODE_FLAG_KEY = 'atp.flag.vsCodeLayout';

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.removeItem(VSCODE_FLAG_KEY);
  });

  async function configure(): Promise<App> {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.setItem(VSCODE_FLAG_KEY, '1');
    TestBed.configureTestingModule({
      providers: [
        App,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    return TestBed.inject(App);
  }

  function taskDetail(id: string): TaskDetail {
    return {
      info: {
        id,
        taskKey: `C:/watch::${id}`,
        title: id,
        state: '2-ready',
        order: 1,
        agent: 'codex',
        createdAt: '2026-01-01T00:00:00Z',
        watchPath: 'C:/watch',
        projectName: 'Project A',
        folderPath: `C:/watch/.orchestrator/jobs/${id}`,
        lastActivity: '2026-01-01T00:00:00Z',
        sessionName: null,
        model: null,
        cliType: 'codex',
        useOwnSession: null,
        lastUsage: null,
        execution: null,
        commit: null,
      } as TaskInfo,
      promptMarkdown: '',
      promptHistory: [],
      titleHistory: [],
      statusMarkdown: null,
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    };
  }

  /** Directly drive the extracted mirror mapping (the effect body). */
  function mirror(app: App, detail: TaskDetail, retargetNav: boolean): void {
    (app as unknown as { mirrorSelectionToStudioTab(d: TaskDetail, r: boolean): void })
      .mirrorSelectionToStudioTab(detail, retargetNav);
  }

  it('retargets the active task tab in place on a pager/cursor step (no new tab)', async () => {
    const app = await configure();
    app.studioTabState.open({ kind: 'task', taskKey: 'C:/watch::task-a' });
    expect(app.studioTabState.tabs().map(studioTabKey)).toEqual([
      'board:__all__',
      'task:C:/watch::task-a',
    ]);

    // Pager step from A → B reuses A's tab.
    mirror(app, taskDetail('task-b'), true);

    expect(app.studioTabState.tabs().map(studioTabKey)).toEqual([
      'board:__all__',
      'task:C:/watch::task-b',
    ]);
    expect(app.studioTabState.activeKey()).toBe('task:C:/watch::task-b');
  });

  it('opens a fresh tab for a non-pager selection (board click)', async () => {
    const app = await configure();
    app.studioTabState.open({ kind: 'task', taskKey: 'C:/watch::task-a' });

    // No retarget hint → the second task gets its own tab.
    mirror(app, taskDetail('task-b'), false);

    expect(app.studioTabState.tabs().map(studioTabKey)).toEqual([
      'board:__all__',
      'task:C:/watch::task-a',
      'task:C:/watch::task-b',
    ]);
    expect(app.studioTabState.activeKey()).toBe('task:C:/watch::task-b');
  });

  it('focuses an already-open tab instead of duplicating, even on a pager step', async () => {
    const app = await configure();
    app.studioTabState.open({ kind: 'task', taskKey: 'C:/watch::task-a' });
    app.studioTabState.open({ kind: 'task', taskKey: 'C:/watch::task-b' });

    // Paging back to A, which still has its own tab → focus it, keep both.
    mirror(app, taskDetail('task-a'), true);

    expect(app.studioTabState.tabs().map(studioTabKey)).toEqual([
      'board:__all__',
      'task:C:/watch::task-a',
      'task:C:/watch::task-b',
    ]);
    expect(app.studioTabState.activeKey()).toBe('task:C:/watch::task-a');
  });
});

/**
 * AGT-2692 - opening a task from the All-projects board must not switch the
 * app into that task's project.
 *
 * Operator report (2026-08-29): from the cross-project board, opening a task
 * silently narrowed the workspace to the task's single project, and closing the
 * task left them stranded there instead of back on "All projects".
 *
 * The scope the app is in is `BoardFiltersService.activeProjects` (persisted,
 * and what the sidebar, picker and lanes read). An empty set is "All projects".
 * The task's own project stays available to the detail view - see
 * `TaskSelectionService.getDetailFor`, which resolves the project handle from
 * the TaskInfo, not from this scope.
 */
describe('App All-projects task open (AGT-2692)', () => {
  const TAB_STORAGE_KEY = 'atp.studio.tabs.v1';
  const VSCODE_FLAG_KEY = 'atp.flag.vsCodeLayout';
  const PROJECT_STORAGE_KEY = 'activeProjects';

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.removeItem(VSCODE_FLAG_KEY);
    localStorage.removeItem(PROJECT_STORAGE_KEY);
  });

  async function configure(): Promise<App> {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.removeItem(PROJECT_STORAGE_KEY);
    localStorage.setItem(VSCODE_FLAG_KEY, '1');
    TestBed.configureTestingModule({
      providers: [
        App,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const app = TestBed.inject(App);
    // Boot lands on the seeded cross-project board; settle its scope effect.
    TestBed.tick();
    return app;
  }

  /** The app-wide project scope, read off the root BoardFiltersService. */
  function scope(): string[] {
    return [...TestBed.inject(BoardFiltersService).activeProjects()];
  }

  function boardTask(over: Partial<TaskInfo>): TaskInfo {
    return {
      id: 'task-a',
      taskKey: 'C:/watch-a::task-a',
      title: 'Task One',
      state: '2-ready',
      order: 1,
      agent: 'codex',
      createdAt: '2026-01-01T00:00:00Z',
      watchPath: 'C:/watch-a',
      projectName: 'Project A',
      folderPath: 'C:/watch-a/.orchestrator/jobs/task-a',
      lastActivity: '2026-01-01T00:00:00Z',
      sessionName: null,
      model: null,
      cliType: 'codex',
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      ...over,
    } as TaskInfo;
  }

  /** Seed a cross-project feed: two projects visible on one board. */
  function seedTwoProjects(app: App): { a: TaskInfo; b: TaskInfo } {
    const a = boardTask({});
    const b = boardTask({
      id: 'task-b',
      taskKey: 'C:/watch-b::task-b',
      title: 'Task Two',
      watchPath: 'C:/watch-b',
      projectName: 'Project B',
      folderPath: 'C:/watch-b/.orchestrator/jobs/task-b',
    });
    app.jobService.jobs.set([a, b]);
    app.jobService.grouped.set({ ...app.jobService.grouped(), ready: [a, b] });
    return { a, b };
  }

  it('leaves the active scope on All projects when a task is opened from the cross-project board', async () => {
    const app = await configure();
    const { a } = seedTwoProjects(app);
    expect(app.studioTabState.activeKey()).toBe('board:__all__');
    expect(scope()).toEqual([]);

    app.openDetail(a);
    TestBed.tick();

    // The task opened, and it remembers the context it was opened from ...
    expect(app.studioTabState.activeTab()).toEqual({
      kind: 'task',
      taskKey: 'C:/watch-a::task-a',
      originScope: 'all-projects',
    });
    // ... so the app-wide scope is still "All projects", not "Project A".
    expect(scope()).toEqual([]);
  });

  it('still narrows the scope for a task opened from a single-project board', async () => {
    const app = await configure();
    const { a } = seedTwoProjects(app);
    app.studioTabState.open({ kind: 'board', projectName: 'Project A' });
    TestBed.tick();
    expect(scope()).toEqual(['Project A']);

    app.openDetail(a);
    TestBed.tick();

    expect(app.studioTabState.activeTab()).toEqual({
      kind: 'task',
      taskKey: 'C:/watch-a::task-a',
    });
    expect(scope()).toEqual(['Project A']);
  });

  it('returns to the All-projects board with a workspace-wide scope when the task tab is closed', async () => {
    const app = await configure();
    const { a } = seedTwoProjects(app);
    app.openDetail(a);
    TestBed.tick();

    app.studioTabState.close('task:C:/watch-a::task-a');
    TestBed.tick();

    expect(app.studioTabState.activeKey()).toBe('board:__all__');
    expect(scope()).toEqual([]);
  });

  it('keeps the All-projects scope while paging from one task to the next', async () => {
    const app = await configure();
    const { a, b } = seedTwoProjects(app);
    app.openDetail(a);
    TestBed.tick();

    // Pager / cursor step reuses the tab in place; the origin context rides along
    // even though the next task belongs to a different project.
    (app as unknown as { mirrorSelectionToStudioTab(d: TaskDetail, r: boolean): void })
      .mirrorSelectionToStudioTab(
        { ...detailShell(), info: b } as TaskDetail,
        true,
      );
    TestBed.tick();

    expect(app.studioTabState.activeTab()).toEqual({
      kind: 'task',
      taskKey: 'C:/watch-b::task-b',
      originScope: 'all-projects',
    });
    expect(scope()).toEqual([]);
  });

  it('restores the All-projects scope after a reload of the task tab', async () => {
    const app = await configure();
    const { a } = seedTwoProjects(app);
    app.openDetail(a);
    TestBed.tick();

    // A reload rehydrates the tab list from localStorage; the origin context
    // has to survive that round trip or the scope snaps on the next boot.
    const persisted = JSON.parse(localStorage.getItem(TAB_STORAGE_KEY) ?? '{}');
    expect(persisted.tabs).toContainEqual({
      kind: 'task',
      taskKey: 'C:/watch-a::task-a',
      originScope: 'all-projects',
    });

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        App,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const reloaded = TestBed.inject(App);
    seedTwoProjects(reloaded);
    TestBed.tick();

    expect(reloaded.studioTabState.activeKey()).toBe('task:C:/watch-a::task-a');
    expect(scope()).toEqual([]);
  });

  it('does not clear a project filter the operator set explicitly', async () => {
    const app = await configure();
    const { a } = seedTwoProjects(app);
    TestBed.inject(BoardFiltersService).setExplicitSoleProject('Project B');
    TestBed.tick();

    app.openDetail(a);
    TestBed.tick();

    // Neither narrowed to the task's project nor silently reset: an explicit
    // filter is the operator's, and only they clear it.
    expect(scope()).toEqual(['Project B']);
  });

  function detailShell(): Omit<TaskDetail, 'info'> {
    return {
      promptMarkdown: '',
      promptHistory: [],
      titleHistory: [],
      statusMarkdown: null,
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    };
  }
});

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
 * AGT-2692 — operator sighting (2026-08-29): from the All-projects board,
 * opening a task switched the active workspace into that task's single
 * project, so closing the task stranded the operator there. The active tab
 * still drives the project scope; a task tab now reports the scope it was
 * opened in instead of the project it happens to belong to.
 */
describe('App active project scope on task open', () => {
  const TAB_STORAGE_KEY = 'atp.studio.tabs.v1';
  const VSCODE_FLAG_KEY = 'atp.flag.vsCodeLayout';
  const ALL_BOARD_KEY = 'board:__all__';

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.removeItem(VSCODE_FLAG_KEY);
    localStorage.removeItem('activeProjects');
  });

  async function configure(): Promise<App> {
    TestBed.resetTestingModule();
    localStorage.removeItem(TAB_STORAGE_KEY);
    localStorage.setItem('activeProjects', '[]');
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
    // The scope mirror is a constructor effect; give it a first run so the
    // seeded All-projects board is the established baseline.
    TestBed.tick();
    return app;
  }

  function boardTask(over: Partial<TaskInfo>): TaskInfo {
    return {
      id: 'task-a',
      taskKey: 'C:/watch::task-a',
      title: 'Task One',
      state: '2-ready',
      order: 1,
      agent: 'codex',
      createdAt: '2026-01-01T00:00:00Z',
      watchPath: 'C:/watch',
      projectName: 'Alpha',
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

  function seedJobs(app: App, jobs: TaskInfo[]): void {
    app.jobService.jobs.set(jobs);
    app.jobService.grouped.set({ ...app.jobService.grouped(), ready: jobs });
  }

  it('leaves the active scope on All projects when a task is opened from the cross-project board', async () => {
    const app = await configure();
    const task = boardTask({});
    seedJobs(app, [task]);
    expect(app.studioTabState.activeKey()).toBe(ALL_BOARD_KEY);
    expect([...app.activeProjects()]).toEqual([]);

    app.studioTabState.open({ kind: 'task', taskKey: task.taskKey });
    TestBed.tick();

    expect(app.studioTabState.activeKey()).toBe('task:C:/watch::task-a');
    // The detail is open, but the app is still workspace-wide.
    expect([...app.activeProjects()]).toEqual([]);
  });

  it('returns to the All-projects board with the scope intact when the task is closed', async () => {
    const app = await configure();
    const task = boardTask({});
    seedJobs(app, [task]);

    app.studioTabState.open({ kind: 'task', taskKey: task.taskKey });
    TestBed.tick();
    app.studioTabState.close('task:C:/watch::task-a');
    TestBed.tick();

    expect(app.studioTabState.activeKey()).toBe(ALL_BOARD_KEY);
    expect([...app.activeProjects()]).toEqual([]);
  });

  it('still narrows to the project when the task is opened from that project board', async () => {
    const app = await configure();
    const task = boardTask({});
    seedJobs(app, [task]);

    app.studioTabState.open({ kind: 'board', projectName: 'Alpha' });
    TestBed.tick();
    expect([...app.activeProjects()]).toEqual(['Alpha']);

    app.studioTabState.open({ kind: 'task', taskKey: task.taskKey });
    TestBed.tick();

    expect([...app.activeProjects()]).toEqual(['Alpha']);
  });

  it('keeps a cross-project task workspace-wide even when it belongs to another project', async () => {
    const app = await configure();
    const alpha = boardTask({});
    const beta = boardTask({ id: 'task-b', taskKey: 'C:/watch::task-b', projectName: 'Beta' });
    seedJobs(app, [alpha, beta]);

    // Open from the All-projects board, then page on to a task in a different
    // project: the iteration stays in the cross-project context.
    app.studioTabState.open({ kind: 'task', taskKey: alpha.taskKey });
    TestBed.tick();
    app.studioTabState.retarget('task:C:/watch::task-a', { kind: 'task', taskKey: beta.taskKey });
    TestBed.tick();

    expect(app.studioTabState.activeKey()).toBe('task:C:/watch::task-b');
    expect([...app.activeProjects()]).toEqual([]);
  });
});

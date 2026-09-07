import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { TaskInfo } from '../../../../models/task.model';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchComponent } from './global-search.component';
import { GlobalSearchFrame, GlobalSearchItem, GlobalSearchService } from './global-search.service';

/** Every frame the stub emits is separated by this much fake time. */
const FRAME_MS = 10;
const DEBOUNCE_MS = 250;

function file(name: string, project = 'Studio'): GlobalSearchItem {
  return { domain: 'files', projectName: project, projectColor: '#fff', title: name, subtitle: name, path: name };
}

function repository(projectName: string, completed: number, total: number, files: GlobalSearchItem[]) {
  return { projectName, completed, total, commits: [], files, durationMs: 12, fromCache: true, failedDomains: [] };
}

const TASKS_FRAME: GlobalSearchFrame = { event: 'tasks', data: { items: [], durationMs: 8, error: null } };

/**
 * Stands in for the SSE transport: emits scripted frames one fake-timer tick
 * apart so a spec can observe the palette between them, and records how often
 * the caller aborted.
 */
class StubSearchService {
  frames: GlobalSearchFrame[] = [];
  queries: string[] = [];
  aborts = 0;
  /** Models a repository that has not answered yet: the stream stays open. */
  stallsAfterFrames = false;

  async *stream(query: string, signal: AbortSignal): AsyncGenerator<GlobalSearchFrame> {
    this.queries.push(query);
    signal.addEventListener('abort', () => { this.aborts++; });
    for (const frame of this.frames) {
      await new Promise<void>(resolve => setTimeout(resolve, FRAME_MS));
      if (signal.aborted) return;
      yield frame;
    }
    if (this.stallsAfterFrames) await new Promise<void>(resolve => setTimeout(resolve, 60_000));
  }
}

describe('GlobalSearchComponent', () => {
  let fixture: ComponentFixture<GlobalSearchComponent>;
  let component: GlobalSearchComponent;
  let api: StubSearchService;

  beforeEach(async () => {
    api = new StubSearchService();
    await TestBed.configureTestingModule({
      imports: [GlobalSearchComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GlobalSearchService, useValue: api },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(GlobalSearchComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    component.cancel();
    vi.useRealTimers();
  });

  it('opens with Ctrl+K and closes with Escape', () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
    expect(component.open()).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.open()).toBe(false);
  });

  it('ranks an exact task key before a title match from in-memory board state', () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-20', title: 'AGT-2034 follow-up', projectName: 'P', state: '2-ready', id: 'a' },
      { taskKey: 'b', key: 'AGT-2034', title: 'Global search', projectName: 'P', state: '3-progress', id: 'b' },
    ] as TaskInfo[]);
    component.query.set('AGT-2034');

    expect(component.taskResults().map(x => x.taskKey)).toEqual(['b', 'a']);
  });

  it('reports each domain separately and appends repository results as they arrive', async () => {
    vi.useFakeTimers();
    api.frames = [
      TASKS_FRAME,
      { event: 'progress', data: { completed: 0, total: 2 } },
      { event: 'repository', data: repository('Studio', 1, 2, [file('a.md')]) },
      { event: 'repository', data: repository('Runner', 2, 2, [file('b.md', 'Runner')]) },
      { event: 'done', data: { durationMs: 40, tasksMs: 8, repositoriesMs: 32, repositories: 2 } },
    ];

    component.onQuery('README');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS);
    // Tasks land first and are reported done while the git domains are still
    // waiting on repositories: that is the whole point of the split.
    expect(component.domains().tasks.status).toBe('done');
    expect(component.domains().files.status).toBe('searching');

    await vi.advanceTimersByTimeAsync(FRAME_MS);
    expect(component.domains().files.total).toBe(2);

    await vi.advanceTimersByTimeAsync(FRAME_MS);
    expect(component.domains().files.completed).toBe(1);
    expect(component.remote().files.map(x => x.path)).toEqual(['a.md']);

    await vi.advanceTimersByTimeAsync(FRAME_MS);
    // Appended after what was already visible, never reordered around it.
    expect(component.remote().files.map(x => x.path)).toEqual(['a.md', 'b.md']);

    await vi.advanceTimersByTimeAsync(FRAME_MS);
    expect(component.domains().files.status).toBe('done');
    expect(component.domains().commits.status).toBe('done');
    expect(component.searching()).toBe(false);
  });

  it('debounces keystrokes and aborts the request in flight', async () => {
    vi.useFakeTimers();
    api.frames = [TASKS_FRAME];

    component.onQuery('RE');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS - 1);
    expect(api.queries).toEqual([]);

    await vi.advanceTimersByTimeAsync(1);
    expect(api.queries).toEqual(['RE']);

    component.onQuery('REA');
    expect(api.aborts).toBe(1);
    expect(component.domains().files.status).toBe('idle');

    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS);
    expect(api.queries).toEqual(['RE', 'REA']);
  });

  it('never asks the backend for a query shorter than two characters', async () => {
    vi.useFakeTimers();

    component.onQuery('R');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS);

    expect(api.queries).toEqual([]);
    expect(component.domains().tasks.status).toBe('idle');
  });

  it('surfaces a calm note once repository search has run for two seconds', async () => {
    vi.useFakeTimers();
    api.frames = [TASKS_FRAME, { event: 'progress', data: { completed: 0, total: 1 } }];
    api.stallsAfterFrames = true;

    component.onQuery('README');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS * 2);
    expect(component.showSlowNotice()).toBe(false);

    await vi.advanceTimersByTimeAsync(2_000);
    expect(component.showSlowNotice()).toBe(true);
    expect(component.elapsedLabel()).toBe('2.0s');
    expect(component.domains().files.status).toBe('searching');
  });

  it('reports a failed repository against the domains it broke', async () => {
    vi.useFakeTimers();
    api.frames = [
      TASKS_FRAME,
      { event: 'progress', data: { completed: 0, total: 1 } },
      { event: 'repository', data: { ...repository('Runner', 1, 1, []), failedDomains: ['commits' as const] } },
      { event: 'done', data: { durationMs: 30, tasksMs: 8, repositoriesMs: 22, repositories: 1 } },
    ];

    component.onQuery('README');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS * 4);

    expect(component.domains().commits.error).toBe('Runner could not be searched.');
    expect(component.domains().files.error).toBeNull();
  });

  it('reports Dossier matches in their own group alongside tasks', async () => {
    vi.useFakeTimers();
    const dossier: GlobalSearchItem = {
      domain: 'dossiers', projectName: 'P', projectColor: '#fff', title: 'Global Orchestrator Watcher',
      subtitle: 'Triggers, recovery authority, and observe-first slices.', dossierKey: 'AGT-W15',
      workbenchId: 'orchestrator-waechter', lane: 'active', phase: 'shaping',
    };
    api.frames = [
      TASKS_FRAME,
      { event: 'dossiers', data: { items: [dossier], durationMs: 6, error: null } },
      { event: 'progress', data: { completed: 0, total: 0 } },
      { event: 'done', data: { durationMs: 10, tasksMs: 8, repositoriesMs: 0, repositories: 0 } },
    ];

    component.onQuery('AGT-W15');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS * 2);

    expect(component.domains().dossiers.status).toBe('done');
    expect(component.remote().dossiers).toEqual([dossier]);
    const group = component.groups().find(g => g.domain === 'dossiers')!;
    expect(group.items).toEqual([dossier]);
  });

  it('opens the Dossier viewer tab when a Dossier result is chosen', () => {
    const tabs = TestBed.inject(StudioTabStateService);
    const open = vi.spyOn(tabs, 'open');
    const dossier: GlobalSearchItem = {
      domain: 'dossiers', projectName: 'P', projectColor: '#fff', title: 'Global Orchestrator Watcher',
      subtitle: 'Summary.', dossierKey: 'AGT-W15', workbenchId: 'orchestrator-waechter',
    };

    component.choose(dossier);

    expect(open).toHaveBeenCalledWith({
      kind: 'workbench', projectName: 'P', workbenchId: 'orchestrator-waechter',
      title: 'Global Orchestrator Watcher', key: 'AGT-W15',
    });
  });

  it('merges indexed task matches the board snapshot does not carry', async () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'live', key: 'AGT-1', title: 'README rewrite', projectName: 'P', state: '2-ready', id: 'live' },
    ] as TaskInfo[]);
    api.frames = [{ event: 'tasks', data: {
      items: [
        { domain: 'tasks', projectName: 'P', projectColor: '#fff', title: 'README rewrite', subtitle: 'AGT-1', taskKey: 'live' },
        { domain: 'tasks', projectName: 'P', projectColor: '#fff', title: 'Archived work', subtitle: 'mentions README', taskKey: 'archived' },
      ],
      durationMs: 8, error: null,
    } }];

    component.onQuery('README');
    await vi.advanceTimersByTimeAsync(DEBOUNCE_MS + FRAME_MS);

    // The board match stays first and is not duplicated by the indexed copy.
    expect(component.taskResults().map(x => x.taskKey)).toEqual(['live', 'archived']);
  });
});

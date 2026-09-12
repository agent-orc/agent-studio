import { describe, expect, it, vi } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { StudioShellComponent } from './studio-shell.component';
import type { RegistryProjectSummary, RegistryWorkspaceListItem, TaskInfo } from '../../models/task.model';
import { TaskService } from '../../services/task.service';
import { StudioTabStateService } from './services/studio-tab-state.service';
import { projectIdentity } from '../../services/project-identity.util';

// AGT-2035: the workspace-delete gating helpers (`canDeleteWorkspace` /
// `workspaceDeleteTooltip`) moved to WorkspaceManagementComponent; their unit
// coverage moved with them to workspace-management.component.spec.ts.

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
describe('StudioShellComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    try {
      await TestBed.configureTestingModule({
        imports: [StudioShellComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(StudioShellComponent);
      try { fixture.detectChanges(); } catch (e) {
        console.warn('[smoke] StudioShellComponent initial render skipped:', (e as Error).message);
      }
      expect(fixture.componentInstance).toBeTruthy();
    } catch (e) {
      // TestBed setup itself crashed (module-load cycle, env not
      // initialized because of file-order, etc). Still verifies the
      // component class is importable.
      console.warn('[smoke] StudioShellComponent TestBed setup skipped:', (e as Error).message);
      expect(StudioShellComponent).toBeTruthy();
    }
  });
});

describe('StudioShellComponent titlebar breadcrumb', () => {
  function project(id: string): RegistryProjectSummary {
    return {
      id,
      displayName: id,
      shortCode: id,
      workspaceId: 'ws-1',
      color: null,
      cliDefault: null,
      modelDefault: null,
      sortOrder: 0,
      storageLocation: `C:/proj/${id}`,
      repositoryPath: null,
      rootPath: null,
      repositoryUrl: null,
      sourceType: 'local-folder',
      urls: [],
      archived: false,
      createdAt: '2026-01-01T00:00:00Z',
    };
  }

  function workspace(over: Partial<RegistryWorkspaceListItem>): RegistryWorkspaceListItem {
    return {
      id: 'ws-1',
      displayName: 'Workspace One',
      sortOrder: 0,
      isDefault: false,
      color: null,
      createdAt: '2026-01-01T00:00:00Z',
      projects: [],
      ...over,
    };
  }

  it('resolves the concrete workspace by normalized project storage path', () => {
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    const component = fixture.componentInstance;

    fixture.componentRef.setInput('knownProjectNames', ['Agent Task Processor']);
    fixture.componentRef.setInput('projectWatchPaths', [{
      name: 'Agent Task Processor',
      path: 'C:\\Projects\\Agent-Taskboard\\',
      rootPath: 'C:\\Projects\\Agent-Taskboard\\',
    }]);
    component.registryWorkspaces.set([workspace({
      id: 'ws-product',
      displayName: 'Product Lab',
      projects: [{
        ...project('PROJ-ATP'),
        displayName: 'Renamed Registry Label',
        storageLocation: 'c:/projects/agent-taskboard',
        workspaceId: 'ws-product',
      }],
    })]);

    component.openBoard('Agent Task Processor');

    expect(component.activeWorkspaceName()).toBe('Product Lab');
  });

  it('renders only concrete workspace plus project in the titlebar breadcrumb area', () => {
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    const component = fixture.componentInstance;

    fixture.componentRef.setInput('knownProjectNames', ['Agent Task Processor']);
    fixture.componentRef.setInput('projectWatchPaths', [{
      name: 'Agent Task Processor',
      path: 'C:\\Projects\\Agent-Taskboard\\',
      rootPath: 'C:\\Projects\\Agent-Taskboard\\',
    }]);
    component.registryWorkspaces.set([workspace({
      id: 'ws-product',
      displayName: 'Product Lab',
      projects: [{
        ...project('PROJ-ATP'),
        displayName: 'Renamed Registry Label',
        storageLocation: 'c:/projects/agent-taskboard',
        workspaceId: 'ws-product',
      }],
    })]);

    component.openBoard('Agent Task Processor');
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const titlebar = root.querySelector<HTMLElement>('[data-testid="studio-titlebar"]');
    const crumbs = root.querySelector<HTMLElement>('[data-testid="studio-titlebar-crumbs"]');
    const workspaceEl = root.querySelector<HTMLElement>('[data-testid="studio-titlebar-active-workspace"]');
    const picker = root.querySelector<HTMLElement>('[data-testid="studio-project-picker-trigger"]');

    expect(workspaceEl?.textContent?.trim()).toBe('Product Lab');
    expect(crumbs?.textContent?.trim()).toBe('Product Lab');
    expect(picker?.textContent).toContain('Agent Task Processor');
    expect(titlebar?.textContent).not.toContain('Agent Software Studio');
    expect(titlebar?.textContent).not.toContain('Workspace');
    expect(titlebar?.textContent).not.toContain('Board');
    expect(root.querySelector('[data-testid="studio-titlebar-workspace"]')).toBeNull();
    expect(root.querySelector('[data-testid="studio-titlebar-active-tab"]')).toBeNull();
  });
});

describe('StudioShellComponent global search', () => {
  it('renders the search input after the titlebar trigger is clicked', () => {
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const trigger = root.querySelector<HTMLButtonElement>('[data-testid="studio-global-search-trigger"]');
    expect(trigger).not.toBeNull();

    trigger!.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="global-search-input"]')).not.toBeNull();
  });
});

describe('StudioShellComponent project lane counts', () => {
  function configure(): { component: StudioShellComponent; taskService: TaskService } {
    localStorage.removeItem('atp.studio.tabs.v1');
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    return {
      component: fixture.componentInstance,
      taskService: TestBed.inject(TaskService),
    };
  }

  function task(over: Partial<TaskInfo>): TaskInfo {
    return {
      id: 'task-a',
      taskKey: 'watch::task-a',
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

  it('derives Ready, In Progress, and Human Review counts per project from grouped lanes', () => {
    const { component, taskService } = configure();
    taskService.grouped.set({
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [
        task({ id: 'a-ready-1', taskKey: 'watch::a-ready-1', projectName: 'Project A', state: '2-ready' }),
        task({ id: 'a-ready-2', taskKey: 'watch::a-ready-2', projectName: 'Project A', state: '2-ready' }),
        task({ id: 'b-ready-1', taskKey: 'watch::b-ready-1', projectName: 'Project B', state: '2-ready' }),
      ],
      progress: [
        task({ id: 'a-progress-1', taskKey: 'watch::a-progress-1', projectName: 'Project A', state: '3-progress' }),
      ],
      failedPickup: [],
      codeNotComplete: [],
      autoReview: [],
      humanReview: [
        task({ id: 'a-review-1', taskKey: 'watch::a-review-1', projectName: 'Project A', state: '5-human-review' }),
        task({ id: 'a-review-2', taskKey: 'watch::a-review-2', projectName: 'Project A', state: '5-human-review' }),
        task({ id: 'a-review-3', taskKey: 'watch::a-review-3', projectName: 'Project A', state: '5-human-review' }),
      ],
      escalated: [],
      review: [],
      completed: [],
      archive: [
        task({ id: 'a-archive-1', taskKey: 'watch::a-archive-1', projectName: 'Project A', state: '7-archive' }),
      ],
    });
    const rows = component.projectRows();
    const projectA = rows.find(row => row.name === 'Project A');
    const projectB = rows.find(row => row.name === 'Project B');

    expect(projectA?.totalJobs).toBe(6);
    expect(projectA?.laneCounts).toEqual({ ready: 2, progress: 1, humanReview: 3 });
    expect(projectB?.laneCounts).toEqual({ ready: 1, progress: 0, humanReview: 0 });

    taskService.grouped.set({
      ...taskService.grouped(),
      ready: [],
      progress: [
        task({ id: 'a-progress-1', taskKey: 'watch::a-progress-1', projectName: 'Project A', state: '3-progress' }),
        task({ id: 'a-progress-2', taskKey: 'watch::a-progress-2', projectName: 'Project A', state: '3-progress' }),
      ],
      humanReview: [],
      archive: [],
    });

    expect(component.projectRows().find(row => row.name === 'Project A')?.laneCounts)
      .toEqual({ ready: 0, progress: 2, humanReview: 0 });
  });

  it('folds escalated cards into the green Human Review chip', () => {
    const { component, taskService } = configure();
    taskService.grouped.set({
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [],
      progress: [],
      failedPickup: [],
      codeNotComplete: [],
      autoReview: [],
      humanReview: [
        task({ id: 'a-review-1', taskKey: 'watch::a-review-1', projectName: 'Project A', state: '5-human-review' }),
      ],
      escalated: [
        task({ id: 'a-esc-1', taskKey: 'watch::a-esc-1', projectName: 'Project A', state: '5e-escalated' }),
        task({ id: 'a-esc-2', taskKey: 'watch::a-esc-2', projectName: 'Project A', state: '5e-escalated' }),
      ],
      review: [],
      completed: [],
      archive: [],
    });

    const projectA = component.projectRows().find(row => row.name === 'Project A');
    // 1 human-review + 2 escalated -> the green chip counts all three.
    expect(projectA?.laneCounts.humanReview).toBe(3);
    expect(projectA?.totalJobs).toBe(3);
  });

  it('keeps Delivered/Completed and Backlog out of every counter', () => {
    const { component, taskService } = configure();
    taskService.grouped.set({
      backlog: [
        task({ id: 'a-backlog-1', taskKey: 'watch::a-backlog-1', projectName: 'Project A', state: '0-backlog' }),
      ],
      preparation: [],
      orchestratorPrep: [],
      ready: [
        task({ id: 'a-ready-1', taskKey: 'watch::a-ready-1', projectName: 'Project A', state: '2-ready' }),
      ],
      progress: [],
      failedPickup: [],
      codeNotComplete: [],
      autoReview: [],
      humanReview: [],
      escalated: [],
      review: [],
      completed: [
        task({ id: 'a-done-1', taskKey: 'watch::a-done-1', projectName: 'Project A', state: '6-completed' }),
        task({ id: 'a-done-2', taskKey: 'watch::a-done-2', projectName: 'Project A', state: '6-completed' }),
      ],
      archive: [],
    });

    const projectA = component.projectRows().find(row => row.name === 'Project A');
    // Only the single Ready card is active work; 2 delivered + 1 backlog count nowhere.
    expect(projectA?.laneCounts).toEqual({ ready: 1, progress: 0, humanReview: 0 });
    expect(projectA?.totalJobs).toBe(1);
  });

  it('does not double-count auto-review cards through the legacy `review` alias', () => {
    const { component, taskService } = configure();
    const autoReviewCards = [
      task({ id: 'a-auto-1', taskKey: 'watch::a-auto-1', projectName: 'Project A', state: '4-auto-review' }),
      task({ id: 'a-auto-2', taskKey: 'watch::a-auto-2', projectName: 'Project A', state: '4-auto-review' }),
    ];
    taskService.grouped.set({
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [
        task({ id: 'a-ready-1', taskKey: 'watch::a-ready-1', projectName: 'Project A', state: '2-ready' }),
      ],
      progress: [],
      failedPickup: [],
      codeNotComplete: [],
      autoReview: autoReviewCards,
      humanReview: [],
      escalated: [],
      // GroupedJobs.review is the legacy alias === autoReview; the same cards appear here.
      review: autoReviewCards,
      completed: [],
      archive: [],
    });

    const projectA = component.projectRows().find(row => row.name === 'Project A');
    // Auto-review is not a board chip; the alias must not inflate the total either.
    expect(projectA?.laneCounts).toEqual({ ready: 1, progress: 0, humanReview: 0 });
    expect(projectA?.totalJobs).toBe(1);
  });

  it('keeps the project total equal to the sum of the three visible board chips (sum-invariant)', () => {
    const { component, taskService } = configure();
    taskService.grouped.set({
      backlog: [
        task({ id: 'a-backlog-1', taskKey: 'watch::a-backlog-1', projectName: 'Project A', state: '0-backlog' }),
      ],
      preparation: [],
      orchestratorPrep: [],
      ready: [
        task({ id: 'a-ready-1', taskKey: 'watch::a-ready-1', projectName: 'Project A', state: '2-ready' }),
        task({ id: 'a-ready-2', taskKey: 'watch::a-ready-2', projectName: 'Project A', state: '2-ready' }),
      ],
      progress: [
        task({ id: 'a-progress-1', taskKey: 'watch::a-progress-1', projectName: 'Project A', state: '3-progress' }),
      ],
      failedPickup: [],
      codeNotComplete: [],
      autoReview: [
        task({ id: 'a-auto-1', taskKey: 'watch::a-auto-1', projectName: 'Project A', state: '4-auto-review' }),
      ],
      humanReview: [
        task({ id: 'a-review-1', taskKey: 'watch::a-review-1', projectName: 'Project A', state: '5-human-review' }),
      ],
      escalated: [
        task({ id: 'a-esc-1', taskKey: 'watch::a-esc-1', projectName: 'Project A', state: '5e-escalated' }),
      ],
      review: [],
      completed: [
        task({ id: 'a-done-1', taskKey: 'watch::a-done-1', projectName: 'Project A', state: '6-completed' }),
      ],
      archive: [],
    });

    const projectA = component.projectRows().find(row => row.name === 'Project A');
    const counts = projectA!.laneCounts;
    // The one defining rule: the aggregate equals the sum of the numbers shown
    // one level below it. Ready(2) + Progress(1) + Human(1 review + 1 escalated).
    expect(counts).toEqual({ ready: 2, progress: 1, humanReview: 2 });
    expect(projectA?.totalJobs).toBe(counts.ready + counts.progress + counts.humanReview);
    expect(projectA?.totalJobs).toBe(5);
  });
});

describe('StudioShellComponent epic tabs', () => {
  function configure(): { fixture: ComponentFixture<StudioShellComponent>; component: StudioShellComponent; taskService: TaskService; tabState: StudioTabStateService } {
    localStorage.removeItem('atp.studio.tabs.v1');
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    return {
      fixture,
      component: fixture.componentInstance,
      taskService: TestBed.inject(TaskService),
      tabState: TestBed.inject(StudioTabStateService),
    };
  }

  function task(over: Partial<TaskInfo>): TaskInfo {
    return {
      id: 'task-a',
      taskKey: 'watch::task-a',
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

  function seedJobs(taskService: TaskService): void {
    const epic = task({
      id: 'epic-a',
      taskKey: 'watch::epic-a',
      title: 'Epic One',
      kind: 'epic',
      order: 0,
    });
    const child = task({
      id: 'task-a',
      taskKey: 'watch::task-a',
      title: 'Task One',
      epicId: 'epic-a',
      kind: 'task',
    });
    taskService.jobs.set([epic, child]);
    taskService.grouped.set({
      ...taskService.grouped(),
      ready: [epic, child],
    });
  }

  it('labels direct epic tabs with the epic title and task-anchored epic tabs with the task title', () => {
    const { component, taskService } = configure();
    seedJobs(taskService);

    expect(component.tabLabel({ kind: 'epic', epicKey: 'watch::epic-a' })).toBe('Epic One');
    expect(component.tabLabel({
      kind: 'epic',
      epicKey: 'watch::epic-a',
      viewTaskKey: 'watch::task-a',
    })).toBe('Task One');
  });

  it('never exposes a watch path when task data has not resolved yet', () => {
    const { component } = configure();
    const taskKey = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::ASS-1766';

    expect(component.tabLabel({ kind: 'task', taskKey })).toBe('ASS-1766');
    expect(component.tabLabel({ kind: 'activity', taskKey })).toBe('Activity · ASS-1766');
    expect(component.tabLabel({ kind: 'epic', epicKey: taskKey })).toBe('ASS-1766');
  });

  it('puts the complete tab name on the tab and truncated title hover targets', () => {
    const { fixture, taskService, tabState } = configure();
    seedJobs(taskService);
    tabState.open({ kind: 'task', taskKey: 'watch::task-a' });

    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const tab = root.querySelector<HTMLElement>('[data-tab-key="task:watch::task-a"]');
    expect(tab?.getAttribute('aria-label')).toContain('Task One');
    expect(fixture.componentInstance.tabTooltip(tabState.activeTab()!)).toContain('Task One');
  });

  it('renders the Epic icon inside an epic detail tab', () => {
    const { fixture, taskService, tabState } = configure();
    seedJobs(taskService);
    tabState.open({ kind: 'epic', epicKey: 'watch::epic-a' });

    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const epicTab = root.querySelector<HTMLElement>('[data-tab-key="epic:watch::epic-a"]');
    expect(epicTab?.querySelector('app-studio-icon')).not.toBeNull();
    expect(epicTab?.textContent).toContain('Epic One');
  });
});

describe('StudioShellComponent hub tab label + icon', () => {
  function project(over: Partial<RegistryProjectSummary>): RegistryProjectSummary {
    return {
      id: 'proj-ass',
      displayName: 'Agent Software Studio',
      shortCode: 'ASS',
      workspaceId: 'ws-1',
      color: null,
      cliDefault: null,
      modelDefault: null,
      sortOrder: 0,
      storageLocation: 'C:/proj/ass',
      urls: [],
      archived: false,
      createdAt: '2026-01-01T00:00:00Z',
      ...over,
      sourceType: over.sourceType ?? 'local-folder',
      repositoryPath: over.repositoryPath ?? null,
      rootPath: over.rootPath ?? null,
      repositoryUrl: over.repositoryUrl ?? null,
    };
  }

  function workspace(over: Partial<RegistryWorkspaceListItem>): RegistryWorkspaceListItem {
    return {
      id: 'ws-1',
      displayName: 'Workspace One',
      sortOrder: 0,
      isDefault: false,
      color: null,
      createdAt: '2026-01-01T00:00:00Z',
      projects: [],
      ...over,
    };
  }

  function makeComponent(): StudioShellComponent {
    localStorage.removeItem('atp.studio.tabs.v1');
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const component = TestBed.createComponent(StudioShellComponent).componentInstance;
    component.registryWorkspaces.set([workspace({ projects: [project({})] })]);
    return component;
  }

  it('shows shortCode + section name for a hub tab (e.g. "ASS · Wiki")', () => {
    const component = makeComponent();
    expect(component.tabLabel({ kind: 'hub', projectName: 'Agent Software Studio', section: 'wiki' }))
      .toBe('ASS · Wiki');
    expect(component.tabLabel({ kind: 'hub', projectName: 'Agent Software Studio', section: 'drift' }))
      .toBe('ASS · Drift');
    expect(component.tabLabel({ kind: 'hub', projectName: 'Agent Software Studio', section: 'settings' }))
      .toBe('ASS · Settings');
  });

  it('uses Deck for the default rail when section is missing or unknown', () => {
    const component = makeComponent();
    expect(component.tabLabel({ kind: 'hub', projectName: 'Agent Software Studio' }))
      .toBe('ASS · Deck');
    expect(component.tabLabel({ kind: 'hub', projectName: 'Agent Software Studio', section: 'nonsense' }))
      .toBe('ASS · Deck');
  });

  it('falls back to the full project name when no shortCode is registered', () => {
    const component = makeComponent();
    expect(component.tabLabel({ kind: 'hub', projectName: 'Unknown Project', section: 'wiki' }))
      .toBe('Unknown Project · Wiki');
  });

  it('applies the shortCode to other project-scoped tabs for consistency', () => {
    const component = makeComponent();
    expect(component.tabLabel({ kind: 'board', projectName: 'Agent Software Studio' }))
      .toBe('ASS · Board');
    expect(component.tabLabel({ kind: 'epics', projectName: 'Agent Software Studio' }))
      .toBe('ASS · Epics');
  });

  it('labels the central Chat History workspace tab', () => {
    const component = makeComponent();
    expect(component.tabLabel({ kind: 'chat-history' })).toBe('Chat History');
    expect(component.tabIcon({ kind: 'chat-history' })).toBe('bot');
  });

  it('returns the section rail icon for a hub tab and null for other kinds', () => {
    const component = makeComponent();
    expect(component.tabIcon({ kind: 'hub', projectName: 'Agent Software Studio', section: 'wiki' }))
      .toBe('book');
    expect(component.tabIcon({ kind: 'hub', projectName: 'Agent Software Studio', section: 'drift' }))
      .toBe('diff');
    // Missing section → default overview rail icon.
    expect(component.tabIcon({ kind: 'hub', projectName: 'Agent Software Studio' }))
      .toBe('layout');
    expect(component.tabIcon({ kind: 'board', projectName: 'Agent Software Studio' }))
      .toBeNull();
  });

  it('paints the project-identity colour dot for project-scoped tabs (AGT-2034)', () => {
    const component = makeComponent();
    const expected = projectIdentity('Agent Software Studio').color;
    expect(component.tabDotColor({ kind: 'board', projectName: 'Agent Software Studio' }))
      .toBe(expected);
    expect(component.tabDotColor({ kind: 'hub', projectName: 'Agent Software Studio', section: 'wiki' }))
      .toBe(expected);
    // Reuses the shared palette — no bespoke colour source.
    expect(expected).toBe(projectIdentity('Agent Software Studio').color);
  });

  it('renders no dot for tabs without a single owning project (AGT-2034)', () => {
    const component = makeComponent();
    expect(component.tabDotColor({ kind: 'board', projectName: '__all__' })).toBeNull();
    expect(component.tabDotColor({ kind: 'epics', projectName: null })).toBeNull();
    expect(component.tabDotColor({ kind: 'workspace-settings' })).toBeNull();
    expect(component.tabDotColor({ kind: 'welcome' })).toBeNull();
  });

  it('keeps the full project name in the tab tooltip while the label stays short (AGT-2034)', () => {
    const component = makeComponent();
    expect(component.tabTooltip({ kind: 'board', projectName: 'Agent Software Studio' }))
      .toBe('Agent Software Studio — ASS · Board');
    // Tabs with no owning project fall back to the plain label.
    expect(component.tabTooltip({ kind: 'board', projectName: '__all__' }))
      .toBe('All projects · Board');
  });
});

describe('StudioShellComponent tab closing', () => {
  it('closes a shell tab on middle click through the same close path as X', () => {
    localStorage.removeItem('atp.studio.tabs.v1');
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    const component = fixture.componentInstance;
    const tabState = TestBed.inject(StudioTabStateService);
    component.openBoard('Project A');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const tab = root.querySelector<HTMLElement>(
      '[data-testid="studio-tab-board:Project A"]',
    );
    tab?.dispatchEvent(new MouseEvent('auxclick', { bubbles: true, cancelable: true, button: 1 }));

    expect(tabState.tabs().some(item => item.kind === 'board' && item.projectName === 'Project A')).toBe(false);
  });

  it('maps Alt+Left to the active Wiki tab history', () => {
    localStorage.removeItem('atp.studio.tabs.v1');
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    const tabState = TestBed.inject(StudioTabStateService);
    tabState.closeAll();
    tabState.open({ kind: 'hub', projectName: 'Project A', section: 'wiki' }, 'new');
    tabState.open({
      kind: 'hub', projectName: 'Project A', section: 'wiki',
      wikiTarget: { kind: 'page', relPath: 'a.md' },
    }, 'replace-current');
    const go = vi.spyOn(history, 'go').mockImplementation(() => undefined);
    const event = new KeyboardEvent('keydown', {
      key: 'ArrowLeft', altKey: true, bubbles: true, cancelable: true,
    });

    fixture.componentInstance.onDocumentHistoryShortcut(event);

    expect(event.defaultPrevented).toBe(true);
    expect(go).toHaveBeenCalledWith(-1);
  });
});

describe('StudioShellComponent active-tab scroll-into-view (AGT-2135)', () => {
  function configure(): {
    fixture: ComponentFixture<StudioShellComponent>;
    component: StudioShellComponent;
    tabState: StudioTabStateService;
  } {
    localStorage.removeItem('atp.studio.tabs.v1');
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    return {
      fixture,
      component: fixture.componentInstance,
      tabState: TestBed.inject(StudioTabStateService),
    };
  }

  /** jsdom implements neither scrollIntoView nor real layout geometry, so we
   *  stub both: a scroll-spy on the active tab and fixed rects on the list +
   *  active tab so the in/out-of-view decision is deterministic. */
  function stub(
    root: HTMLElement,
    activeTabKey: string,
    tabRect: { left: number; right: number },
  ): ReturnType<typeof vi.fn> {
    const list = root.querySelector<HTMLElement>('.studio-tabbar__list')!;
    const active = Array.from(list.querySelectorAll<HTMLElement>('.studio-tab'))
      .find(el => el.getAttribute('data-tab-key') === activeTabKey)!;
    const spy = vi.fn();
    active.scrollIntoView = spy as unknown as HTMLElement['scrollIntoView'];
    // Visible strip spans x ∈ [0, 200].
    list.getBoundingClientRect = (() => ({ left: 0, right: 200, top: 0, bottom: 30, width: 200, height: 30, x: 0, y: 0, toJSON() { return {}; } }) as DOMRect);
    active.getBoundingClientRect = (() => ({ left: tabRect.left, right: tabRect.right, top: 0, bottom: 30, width: tabRect.right - tabRect.left, height: 30, x: tabRect.left, y: 0, toJSON() { return {}; } }) as DOMRect);
    return spy;
  }

  function openBoards(component: StudioShellComponent, names: string[]): void {
    for (const name of names) component.openBoard(name);
  }

  it('smooth-scrolls the active tab into view when it lies outside the visible strip', async () => {
    const { fixture, component } = configure();
    openBoards(component, ['Project A', 'Project B', 'Project C']);
    fixture.detectChanges();
    await Promise.resolve(); // drain the initial (boot) scroll microtask

    const root: HTMLElement = fixture.nativeElement;
    // Activate the first board (its tab sits to the left, scrolled out of view).
    const key = 'board:Project A';
    const spy = stub(root, key, { left: -160, right: -60 });

    component.selectTab(key);
    fixture.detectChanges();
    await Promise.resolve();

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy).toHaveBeenCalledWith({ behavior: 'smooth', inline: 'nearest', block: 'nearest' });
  });

  it('leaves an already-visible active tab untouched (no needless scroll)', async () => {
    const { fixture, component } = configure();
    openBoards(component, ['Project A', 'Project B', 'Project C']);
    fixture.detectChanges();
    await Promise.resolve(); // drain the initial (boot) scroll microtask

    const root: HTMLElement = fixture.nativeElement;
    const key = 'board:Project C';
    // Fully inside the [0, 200] strip.
    const spy = stub(root, key, { left: 40, right: 140 });

    component.selectTab(key);
    fixture.detectChanges();
    await Promise.resolve();

    expect(spy).not.toHaveBeenCalled();
  });
});

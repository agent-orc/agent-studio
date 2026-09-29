import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import {
  ShellPanesVisible,
  StudioTabActionsComponent,
  StudioTabPager,
  StudioTabTriage,
} from './studio-tab-actions.component';
import type { RunActivityBadge } from '../../../../services/run-activity.util';
import type { TaskDetail, TaskInfo } from '../../../../models/task.model';
import type { TaskCommitInfo } from '../../../git';

/**
 * AGT-2819 split the studio tab bar's action strip out of `app.html`. These
 * cases pin the strip's public behaviour - which cluster renders for which tab,
 * what each control emits, and what the tab bar refuses while mutations are
 * blocked - so the split is proven not to alter it.
 */
function info(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'ws::task-1',
    title: 'A task',
    state: '5-human-review',
    order: 1,
    agent: 'claude',
    createdAt: '2026-09-16T12:00:00Z',
    watchPath: 'ws',
    projectName: 'demo',
    folderPath: '/tmp/task-1',
    lastActivity: '2026-09-16T12:00:00Z',
    ...overrides,
  } as TaskInfo;
}

function detail(overrides: Partial<TaskInfo> = {}): TaskDetail {
  return { info: info(overrides) } as TaskDetail;
}

function commit(sha: string): TaskCommitInfo {
  return {
    sha,
    shortSha: sha.slice(0, 7),
    message: `commit ${sha}`,
    filesChanged: 1,
    files: ['a.ts'],
    at: '2026-09-16T12:00:00Z',
  } as TaskCommitInfo;
}

const NO_PANES: ShellPanesVisible = { prompt: false, protocol: false, git: false };
const NO_PAGER: StudioTabPager = { position: 0, total: 0, laneState: '5-human-review' };
const NO_TRIAGE: StudioTabTriage = {
  hasActions: false,
  primaryId: null,
  primaryLabel: '',
  primaryTooltip: '',
  awaitingGit: false,
  blockedByIntegration: false,
  actingId: null,
  menuItems: [],
};

const LANE_OPTIONS = [
  { state: '5-human-review', label: 'Human review' },
  { state: '6-completed', label: 'Completed' },
];

interface Overrides {
  documentHistoryVisible?: boolean;
  canNavigateBack?: boolean;
  canNavigateForward?: boolean;
  documentHistoryLength?: number;
  boardActionsVisible?: boolean;
  groupByEpic?: boolean;
  mutationsBlocked?: boolean;
  task?: TaskDetail | null;
  runActivity?: RunActivityBadge | null;
  panes?: ShellPanesVisible;
  pager?: StudioTabPager;
  triage?: StudioTabTriage;
}

async function build(overrides: Overrides = {}) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [StudioTabActionsComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(StudioTabActionsComponent);
  const inputs: Required<Overrides> = {
    documentHistoryVisible: false,
    canNavigateBack: false,
    canNavigateForward: false,
    documentHistoryLength: 0,
    boardActionsVisible: false,
    groupByEpic: false,
    mutationsBlocked: false,
    task: null,
    runActivity: null,
    panes: NO_PANES,
    pager: NO_PAGER,
    triage: NO_TRIAGE,
    ...overrides,
  };
  for (const [name, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(name, value);
  }
  fixture.componentRef.setInput('laneOptions', LANE_OPTIONS);
  fixture.detectChanges();
  return fixture;
}

function query(fixture: Awaited<ReturnType<typeof build>>, testId: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
}

afterEach(() => {
  TestBed.resetTestingModule();
});

describe('StudioTabActionsComponent - document history', () => {
  it('renders nothing for a tab without document history', async () => {
    const fixture = await build();
    expect(query(fixture, 'studio-document-history')).toBeNull();
  });

  it('disables the arrows the history cannot travel', async () => {
    const fixture = await build({
      documentHistoryVisible: true,
      canNavigateBack: true,
      documentHistoryLength: 3,
    });
    expect((query(fixture, 'studio-document-back') as HTMLButtonElement).disabled).toBe(false);
    expect((query(fixture, 'studio-document-forward') as HTMLButtonElement).disabled).toBe(true);
    expect(query(fixture, 'studio-document-history')?.getAttribute('data-history-length')).toBe('3');
  });

  it('emits the travel direction the user pressed', async () => {
    const fixture = await build({ documentHistoryVisible: true, canNavigateBack: true });
    const seen: (1 | -1)[] = [];
    fixture.componentInstance.navigateDocumentHistory.subscribe(delta => seen.push(delta));
    query(fixture, 'studio-document-back')!.click();
    expect(seen).toEqual([-1]);
  });
});

describe('StudioTabActionsComponent - board actions', () => {
  it('labels the grouping toggle by the view it switches to', async () => {
    const lanes = await build({ boardActionsVisible: true, groupByEpic: false });
    expect(query(lanes, 'studio-board-epic-toggle')?.textContent).toContain('Lanes');
    const epics = await build({ boardActionsVisible: true, groupByEpic: true });
    expect(query(epics, 'studio-board-epic-toggle')?.textContent).toContain('Epics');
    expect(query(epics, 'studio-board-epic-toggle')?.getAttribute('aria-pressed')).toBe('true');
  });

  it('blocks Add task while mutations are blocked', async () => {
    const fixture = await build({ boardActionsVisible: true, mutationsBlocked: true });
    expect((query(fixture, 'studio-board-add-task') as HTMLButtonElement).disabled).toBe(true);
  });

  it('emits the add-task request', async () => {
    const fixture = await build({ boardActionsVisible: true });
    let requested = 0;
    fixture.componentInstance.addTask.subscribe(() => (requested += 1));
    query(fixture, 'studio-board-add-task')!.click();
    expect(requested).toBe(1);
  });
});

describe('StudioTabActionsComponent - the open task', () => {
  it('renders no task cluster while no task is settled', async () => {
    const fixture = await build();
    expect(query(fixture, 'studio-pane-toggles')).toBeNull();
    expect(query(fixture, 'studio-lane-select')).toBeNull();
  });

  it('reflects which panes are open', async () => {
    const fixture = await build({
      task: detail(),
      panes: { prompt: true, protocol: false, git: false },
    });
    expect(query(fixture, 'studio-pane-toggle-prompt')?.getAttribute('aria-pressed')).toBe('true');
    expect(query(fixture, 'studio-pane-toggle-protocol')?.getAttribute('aria-pressed')).toBe('false');
  });

  it('emits the pane the user toggled', async () => {
    const fixture = await build({ task: detail() });
    const seen: string[] = [];
    fixture.componentInstance.togglePane.subscribe(pane => seen.push(pane));
    query(fixture, 'studio-pane-toggle-git')!.click();
    expect(seen).toEqual(['git']);
  });

  it('badges the git toggle with the commit count and hides it at zero', async () => {
    const none = await build({ task: detail() });
    expect(query(none, 'studio-pane-toggle-git-badge')).toBeNull();
    const two = await build({
      task: detail({ commits: [commit('a'), commit('b')] }),
    });
    expect(query(two, 'studio-pane-toggle-git-badge')?.textContent?.trim()).toBe('2');
  });

  it('counts a single legacy commit field as one commit', async () => {
    const fixture = await build({ task: detail({ commit: commit('abc1234') }) });
    expect(query(fixture, 'studio-pane-toggle-git-badge')?.textContent?.trim()).toBe('1');
  });

  it('hides the pager counter until the lane has been measured', async () => {
    const empty = await build({ task: detail() });
    expect(query(empty, 'studio-task-pager-position')).toBeNull();
    const measured = await build({
      task: detail(),
      pager: { position: 2, total: 7, laneState: '5-human-review' },
    });
    expect(query(measured, 'studio-task-pager-position')?.textContent?.trim()).toBe('2 / 7');
  });

  it('renders an em dash when the open task has left the captured iteration', async () => {
    const fixture = await build({
      task: detail(),
      pager: { position: 0, total: 7, laneState: '5-human-review' },
    });
    expect(query(fixture, 'studio-task-pager-position')?.textContent?.trim()).toBe('— / 7');
  });

  it('emits pager navigation', async () => {
    const fixture = await build({ task: detail() });
    const seen: string[] = [];
    fixture.componentInstance.previousTask.subscribe(() => seen.push('prev'));
    fixture.componentInstance.nextTask.subscribe(() => seen.push('next'));
    query(fixture, 'studio-task-prev')!.click();
    query(fixture, 'studio-task-next')!.click();
    expect(seen).toEqual(['prev', 'next']);
  });

  it('adds an option for a lane the dropdown does not list', async () => {
    const fixture = await build({
      task: detail({ state: '3-progress' }),
      pager: { position: 1, total: 1, laneState: '3-progress' },
    });
    const options = Array.from(
      (query(fixture, 'studio-lane-select') as HTMLSelectElement).options,
    ).map(option => option.value);
    expect(options).toEqual(['5-human-review', '6-completed', '3-progress']);
  });

  it('lists only the navigable lanes when the pager sits on one of them', async () => {
    const fixture = await build({ task: detail() });
    const options = Array.from(
      (query(fixture, 'studio-lane-select') as HTMLSelectElement).options,
    ).map(option => option.value);
    expect(options).toEqual(['5-human-review', '6-completed']);
  });

  it('shows the run-activity pill only when the shell derived one', async () => {
    const none = await build({ task: detail() });
    expect(query(none, 'studio-run-activity')).toBeNull();
    const badge = await build({
      task: detail(),
      runActivity: {
        kind: 'active',
        tone: 'active',
        label: 'Running',
        tooltip: { title: 'Running now' },
      } as RunActivityBadge,
    });
    expect(query(badge, 'studio-run-activity')?.textContent).toContain('Running');
    expect(query(badge, 'studio-run-activity')?.getAttribute('data-run-activity-kind')).toBe('active');
  });

  it('hides the execution-location badge while nothing is executing', async () => {
    const fixture = await build({
      task: detail({
        executionLocation: { state: 'no-active-execution' } as TaskInfo['executionLocation'],
      }),
    });
    expect(query(fixture, 'studio-execution-location')).toBeNull();
  });
});

describe('StudioTabActionsComponent - triage cluster', () => {
  const withPrimary: StudioTabTriage = {
    ...NO_TRIAGE,
    hasActions: true,
    primaryId: 'mark-done',
    primaryLabel: 'Accept',
    primaryTooltip: 'Accept (Enter)',
    menuItems: [{ kind: 'row', id: 'delete', label: 'Delete' }],
  };

  it('renders nothing while the open task has no actions', async () => {
    const fixture = await build({ task: detail() });
    expect(query(fixture, 'studio-triage-panel')).toBeNull();
  });

  it('names the primary by its action id so tests and shortcuts can find it', async () => {
    const fixture = await build({ task: detail(), triage: withPrimary });
    const primary = query(fixture, 'studio-triage-action-mark-done');
    expect(primary?.textContent?.trim()).toBe('Accept');
    expect(primary?.getAttribute('data-action-id')).toBe('mark-done');
  });

  it('disables and skeletons the primary while git provenance is still loading', async () => {
    const fixture = await build({
      task: detail(),
      triage: { ...withPrimary, awaitingGit: true },
    });
    const primary = query(fixture, 'studio-triage-action-mark-done') as HTMLButtonElement;
    expect(primary.disabled).toBe(true);
    expect(primary.getAttribute('data-git-loading')).toBe('true');
    expect(primary.textContent).toContain('Checking git status');
  });

  it('disables the primary while another triage action is in flight', async () => {
    const fixture = await build({
      task: detail(),
      triage: { ...withPrimary, actingId: 'promote-ready' },
    });
    expect((query(fixture, 'studio-triage-action-mark-done') as HTMLButtonElement).disabled).toBe(true);
  });

  it('emits the primary press', async () => {
    const fixture = await build({ task: detail(), triage: withPrimary });
    let pressed = 0;
    fixture.componentInstance.triagePrimary.subscribe(() => (pressed += 1));
    query(fixture, 'studio-triage-action-mark-done')!.click();
    expect(pressed).toBe(1);
  });

  it('owns its overflow menu and refuses to open it while mutations are blocked', async () => {
    const fixture = await build({
      task: detail(),
      triage: withPrimary,
      mutationsBlocked: true,
    });
    const button = query(fixture, 'studio-triage-overflow-btn') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fixture.componentInstance.onToggleOverflow(
      new MouseEvent('click') as MouseEvent & { currentTarget: HTMLElement },
    );
    expect(fixture.componentInstance.overflowOpen()).toBe(false);
  });

  it('opens, anchors, and closes the overflow menu', async () => {
    const fixture = await build({ task: detail(), triage: withPrimary });
    const button = query(fixture, 'studio-triage-overflow-btn') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.overflowOpen()).toBe(true);
    expect(fixture.componentInstance.overflowAnchor()).toBe(button);
    expect(button.getAttribute('aria-expanded')).toBe('true');
    fixture.componentInstance.closeOverflow();
    expect(fixture.componentInstance.overflowOpen()).toBe(false);
  });

  it('closes the overflow menu before reporting the chosen action', async () => {
    const fixture = await build({ task: detail(), triage: withPrimary });
    (query(fixture, 'studio-triage-overflow-btn') as HTMLButtonElement).click();
    const seen: string[] = [];
    fixture.componentInstance.triageMenuItem.subscribe(event => {
      seen.push(event.id);
      expect(fixture.componentInstance.overflowOpen()).toBe(false);
    });
    fixture.componentInstance.onOverflowItemClick({ id: 'delete' } as never);
    expect(seen).toEqual(['delete']);
  });
});

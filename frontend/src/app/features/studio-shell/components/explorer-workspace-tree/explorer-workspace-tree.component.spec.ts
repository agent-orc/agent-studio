import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import {
  ExplorerWorkspaceTreeComponent,
  type ExplorerProjectRow,
} from './explorer-workspace-tree.component';
import type { ProjectAutoPickupIndicator, ProjectAutoPickupState } from '../../studio-shell.auto-pickup';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { RegistryWorkspaceListItem, RegistryProjectSummary } from '../../../../models/task.model';
import { ExplorerSectionsService } from '../../services/explorer-sections.service';

function project(displayName: string, workspaceId: string, storage: string): RegistryProjectSummary {
  return {
    sourceType: 'local-folder',
    id: `PROJ-${displayName}`,
    displayName,
    shortCode: displayName.slice(0, 3).toUpperCase(),
    workspaceId,
    color: null,
    cliDefault: null,
    modelDefault: null,
    sortOrder: 0,
    storageLocation: storage,
    repositoryPath: null,
    rootPath: null,
    repositoryUrl: null,
    urls: [],
    archived: false,
    createdAt: '2026-01-01T00:00:00Z',
  };
}

function workspace(
  id: string,
  displayName: string,
  sortOrder: number,
  projects: RegistryProjectSummary[],
): RegistryWorkspaceListItem {
  return { id, displayName, sortOrder, isDefault: id === 'ws-default', color: null, createdAt: '2026-01-01T00:00:00Z', projects };
}

function row(name: string, over: Partial<ExplorerProjectRow> = {}): ExplorerProjectRow {
  return {
    name,
    initial: name[0]?.toUpperCase() ?? '?',
    color: '#888',
    totalJobs: 0,
    isActive: false,
    ...over,
  };
}

function mount() {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  return TestBed.createComponent(ExplorerWorkspaceTreeComponent);
}

const noop = (): void => { return; };

describe('ExplorerWorkspaceTreeComponent', () => {
  it('constructs and wires its injected services', () => {
    const fixture = mount();
    expect(fixture.componentInstance).toBeTruthy();
    try {
      fixture.detectChanges();
    } catch (err) {
      console.warn('render path note:', err);
    }
  });

  it('groups project rows under their registry workspace by display name', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
      workspace('ws-2', 'Side Projects', 1, [project('Beta', 'ws-2', '/repos/Beta')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);

    const groups = cmp.groups();
    expect(groups.map(g => g.displayName)).toEqual(['Default', 'Side Projects']);
    expect(groups[0].projects.map(p => p.name)).toEqual(['Alpha']);
    expect(groups[1].projects.map(p => p.name)).toEqual(['Beta']);
  });

  it('matches a renamed workspace project by its storage-folder tail', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    // Registry displayName diverges from the row name, but the folder
    // tail of storageLocation still matches the (folder-derived) row.
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Renamed', 'ws-default', 'C:/repos/Gamma')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Gamma')]);

    const groups = cmp.groups();
    expect(groups).toHaveLength(1);
    expect(groups[0].projects.map(p => p.name)).toEqual(['Gamma']);
  });

  it('puts rows with no registry match into an Unassigned group', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Orphan')]);

    const groups = cmp.groups();
    expect(groups.map(g => g.id)).toEqual(['ws-default', '__unassigned__']);
    expect(groups[1].projects.map(p => p.name)).toEqual(['Orphan']);
  });

  it('keeps a registered project with no valid workspace draggable in Unassigned', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    const orphan = project('AI Patterns', '', '/repos/AI Patterns');
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, []),
    ]);
    fixture.componentRef.setInput('registryProjects', [orphan]);
    fixture.componentRef.setInput('projectRows', [row('AI Patterns')]);

    const unassigned = cmp.groups().find(group => group.id === '__unassigned__');
    expect(unassigned?.projects[0]).toMatchObject({
      name: 'AI Patterns',
      projectId: orphan.id,
      workspaceId: null,
    });

    fixture.detectChanges();
    const dragRow = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-project-row-AI Patterns"]',
    ) as HTMLElement | null;
    expect(dragRow?.classList.contains('cdk-drag-disabled')).toBe(false);
  });

  it('shows registration guidance only for genuinely registry-less rows', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, []),
    ]);
    fixture.componentRef.setInput('registryProjects', []);
    fixture.componentRef.setInput('projectRows', [row('Local only')]);
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const hintTestId = 'studio-explorer-project-register-hint-Local only';
    expect(root.querySelector(`[data-testid="${hintTestId}"]`)?.textContent)
      .toContain('Register first');
    const hint = fixture.debugElement
      .queryAll(By.directive(TooltipDirective))
      .find(node => (node.nativeElement as HTMLElement).dataset['testid'] === hintTestId);
    expect(hint?.injector.get(TooltipDirective).content())
      .toBe('Use + on the destination workspace to onboard this project');
  });

  it('falls back to a single legacy folder when the registry is empty', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);

    const groups = cmp.groups();
    expect(groups).toHaveLength(1);
    expect(groups[0].id).toBe('__all__');
    expect(groups[0].projects.map(p => p.name)).toEqual(['Alpha', 'Beta']);
  });

  it('reveals the workspace path that owns the active Dossier', () => {
    const fixture = mount();
    const sections = TestBed.inject(ExplorerSectionsService);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    sections.setCollapsed('workspace', true);
    sections.setCollapsed('ws:ws-default', true);

    fixture.componentRef.setInput('activeWorkbench', {
      projectName: 'Alpha',
      workbenchId: 'routing-lab',
    });
    fixture.componentRef.setInput('activeProjectSurface', 'workbench');
    fixture.detectChanges();

    expect(sections.isCollapsed('workspace')).toBe(false);
    expect(sections.isCollapsed('ws:ws-default')).toBe(false);
  });

  it('reveals the owning workspace for a regular project destination without changing other branches', () => {
    const fixture = mount();
    const sections = TestBed.inject(ExplorerSectionsService);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
      workspace('ws-other', 'Other', 1, [project('Beta', 'ws-other', '/repos/Beta')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true }), row('Beta')]);
    sections.setCollapsed('workspace', true);
    sections.setCollapsed('ws:ws-default', true);
    sections.setCollapsed('ws:ws-other', true);

    fixture.componentRef.setInput('activeProjectSurface', 'wiki');
    fixture.detectChanges();

    expect(sections.isCollapsed('workspace')).toBe(false);
    expect(sections.isCollapsed('ws:ws-default')).toBe(false);
    expect(sections.isCollapsed('ws:ws-other')).toBe(true);

    sections.setCollapsed('ws:ws-default', true);
    fixture.detectChanges();
    expect(sections.isCollapsed('ws:ws-default')).toBe(true);

    sections.setCollapsed('workspace', false);
    sections.setCollapsed('ws:ws-default', false);
    sections.setCollapsed('ws:ws-other', false);
  });

  it('collapse-all closes the tree head and every workspace branch', () => {
    const fixture = mount();
    const sections = TestBed.inject(ExplorerSectionsService);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
      workspace('ws-other', 'Other', 1, [project('Beta', 'ws-other', '/repos/Beta')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);
    fixture.detectChanges();

    fixture.componentRef.setInput('collapseAllVersion', 1);
    fixture.detectChanges();

    expect(sections.isCollapsed('workspace')).toBe(true);
    expect(sections.isCollapsed('ws:ws-default')).toBe(true);
    expect(sections.isCollapsed('ws:ws-other')).toBe(true);

    sections.setCollapsed('workspace', false);
    sections.setCollapsed('ws:ws-default', false);
    sections.setCollapsed('ws:ws-other', false);
  });

  it('renders Board lane counters with a descriptive aria label', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [
      row('Alpha', { laneCounts: { ready: 3, progress: 2, humanReview: 5 } }),
    ]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));

    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const board = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-Alpha"]');
    const counts = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-counts-Alpha"]');
    const ready = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-count-ready-Alpha"]');
    const progress = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-count-progress-Alpha"]');
    const review = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-count-human-review-Alpha"]');

    expect(board?.getAttribute('aria-label')).toBe('Board, 3 ready, 2 in progress, 5 human review');
    expect(counts?.getAttribute('aria-label')).toBe('3 ready, 2 in progress, 5 human review');
    expect(ready?.textContent?.trim()).toBe('3');
    expect(progress?.textContent?.trim()).toBe('2');
    expect(review?.textContent?.trim()).toBe('5');
  });

  it('attaches a lane-explaining cacTooltip to each Board lane counter', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [
      row('Alpha', { laneCounts: { ready: 3, progress: 2, humanReview: 5 } }),
    ]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const tipFor = (testid: string) => {
      const node = fixture.debugElement
        .queryAll(By.directive(TooltipDirective))
        .find(d => (d.nativeElement as HTMLElement).getAttribute('data-testid') === testid);
      return node?.injector.get(TooltipDirective).content();
    };

    expect(tipFor('studio-explorer-project-board-count-ready-Alpha')).toMatchObject({ title: 'Ready' });
    expect(tipFor('studio-explorer-project-board-count-progress-Alpha')).toMatchObject({ title: 'In Progress' });
    // AGT-2715: one wording for the lane, everywhere.
    expect(tipFor('studio-explorer-project-board-count-human-review-Alpha')).toMatchObject({ title: 'Human review' });
  });

  it('renders a capped, stably ordered dot dashboard with numeric a11y text', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [
      row('Alpha', { totalJobs: 20, laneCounts: { ready: 4, progress: 3, humanReview: 13 } }),
    ]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.componentRef.setInput('metricView', 'dots');
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const dashboard = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-dots-Alpha"]');
    const dots = Array.from(dashboard?.querySelectorAll<HTMLElement>('[data-lane]') ?? []);

    expect(root.querySelector('[data-testid="studio-explorer-project-board-counts-Alpha"]')).toBeNull();
    expect(dashboard?.getAttribute('aria-label')).toBe('4 ready, 3 in progress, 13 human review');
    expect(dots).toHaveLength(15);
    expect(dots.map(dot => dot.dataset['lane'])).toEqual([
      ...Array(4).fill('ready'),
      ...Array(3).fill('progress'),
      ...Array(8).fill('humanReview'),
    ]);
    expect(dashboard?.querySelector('.studio-board-lane-dots__overflow')?.textContent?.trim()).toBe('+5');
  });

  it('renders create workspace as a compact Workspaces header action', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    const emitted: void[] = [];
    cmp.onboardWorkspaceRequest.subscribe(v => emitted.push(v));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const add = root.querySelector<HTMLButtonElement>('[data-testid="studio-explorer-add-workspace"]');
    expect(add?.classList.contains('studio-sidebar-header__action')).toBe(true);
    expect(add?.querySelector('app-studio-icon')).toBeTruthy();

    add?.click();

    expect(emitted).toHaveLength(1);
    expect(cmp.isCollapsed('workspace')).toBe(false);
  });

  it('renders create project as a compact workspace-row icon action', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    cmp.setCollapsed('ws:ws-default', false);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    const emitted: string[] = [];
    cmp.onboardProjectRequest.subscribe(v => emitted.push(v));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const add = root.querySelector<HTMLButtonElement>('[data-testid="studio-workspace-ws-default-add-project"]');
    expect(add?.tagName.toLowerCase()).toBe('button');
    expect(add?.classList.contains('tree-row')).toBe(false);
    expect(add?.querySelector('app-studio-icon')).toBeTruthy();
    expect(root.textContent).not.toContain('New project');

    add?.click();

    expect(emitted).toEqual(['ws-default']);
    expect(cmp.isCollapsed('ws:ws-default')).toBe(false);
  });

  it('highlights the active project subnavigation row', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.componentRef.setInput('activeProjectSurface', 'hub');
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const board = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-Alpha"]');
    const hub = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-hub-Alpha"]');
    const deck = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-deck-Alpha"]');

    expect(board?.classList.contains('tree-row--active')).toBe(false);
    expect(board?.getAttribute('aria-current')).toBeNull();
    expect(hub?.classList.contains('tree-row--active')).toBe(true);
    expect(hub?.getAttribute('aria-current')).toBe('page');
    expect(deck?.textContent).toContain('Deck');
    expect(deck?.querySelector('button')?.getAttribute('aria-label')).toBe('Deck, Alpha');
    expect(deck?.querySelector('[data-testid="studio-explorer-project-hub-Alpha"]')).toBe(hub);
    const deckIcon = deck?.querySelector('.tree-row__glyph-icon svg');
    expect(deckIcon?.getAttribute('viewBox')).toBe('0 0 24 24');
    expect(deckIcon?.querySelector('path')?.getAttribute('d')).toBe('M9 3v18M9 10h12');
    expect(deckIcon?.querySelector('circle')?.getAttribute('cy')).toBe('15.5');
    // The destinations are the third tree level: they render as `tree-row--child`
    // (one indent step below the project row), never flush `--root`. Guards the
    // AGT-2057 regression where AGT-2037 flattened them to `--root`.
    expect(board?.classList.contains('tree-row--child')).toBe(true);
    expect(hub?.classList.contains('tree-row--child')).toBe(true);
    expect(root.querySelector('.studio-tree-children .tree-row--root')).toBeNull();
  });

  it('gives project and URL names priority while keeping complete tooltip context', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Agent Orchestration Studio')]);
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const projectRow = root.querySelector<HTMLElement>(
      '[data-testid="studio-explorer-project-Agent Orchestration Studio"]',
    );
    expect(projectRow?.querySelector('app-count-badge')).toBeNull();
    expect(projectRow?.getAttribute('aria-label')).toContain('Agent Orchestration Studio, 0 open tasks');

    const node = cmp.groups()[0].projects[0];
    expect(cmp.projectTooltip(node)).toContain(
      'Agent Orchestration Studio\n0 open tasks\nAuto-pickup manual',
    );
    expect(cmp.urlTooltip(node, {
      id: 'preview',
      label: 'Public development preview',
      url: 'https://example.test/preview',
      sortOrder: 0,
      startRule: null,
    })).toBe('Public development preview\nhttps://example.test/preview\nStatus unknown');
  });

  it('insets every project destination one level below the project row (AGT-2057 regression)', () => {
    // Explorer hierarchy is workspace -> project -> destinations. The project
    // row is `level="root"`; its Board / Deck / Wiki / Epics (+ URL)
    // destinations must be `level="child"` so they nest visibly one step in and
    // the project reads as clearly superordinate. AGT-2037 flattened them to
    // `level="root"`, so they rendered flush beside the project ("Kuddelmuddel",
    // looked like siblings). A unit test that had been updated to assert the
    // flat layout let the regression ship; this locks the correct nesting.
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [
      row('Alpha', { isActive: true }),
    ]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;

    // The project row itself stays at the root level.
    const projectRow = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-Alpha"]');
    expect(projectRow?.classList.contains('tree-row--root')).toBe(true);
    expect(projectRow?.classList.contains('tree-row--child')).toBe(false);

    // Every destination under the expanded project is indented one level.
    for (const surface of ['board', 'hub', 'wiki', 'epics']) {
      const dest = root.querySelector<HTMLElement>(`[data-testid="studio-explorer-project-${surface}-Alpha"]`);
      expect(dest, surface).toBeTruthy();
      expect(dest?.classList.contains('tree-row--child'), surface).toBe(true);
      expect(dest?.classList.contains('tree-row--root'), surface).toBe(false);
    }

    // Nothing inside the children container may render at the flat root level.
    expect(root.querySelector('.studio-tree-children .tree-row--root')).toBeNull();
    expect(
      root.querySelectorAll('.studio-tree-children .tree-row--child').length,
    ).toBeGreaterThanOrEqual(4);
  });

  it('renders a Wiki row under Deck that emits openWikiRequest', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const emitted: string[] = [];
    cmp.openWikiRequest.subscribe(name => emitted.push(name));

    const root: HTMLElement = fixture.nativeElement;
    const rows = Array.from(
      root.querySelectorAll<HTMLElement>('.studio-tree-children .tree-row[data-testid^="studio-explorer-project-"]'),
    ).map(el => el.getAttribute('data-testid'));
    // Wiki sits directly after Deck in the per-project child list.
    expect(rows).toEqual([
      'studio-explorer-project-board-Alpha',
      'studio-explorer-project-hub-Alpha',
      'studio-explorer-project-wiki-Alpha',
      'studio-explorer-project-workbenches-Alpha',
      'studio-explorer-project-epics-Alpha',
    ]);

    const wiki = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-wiki-Alpha"]');
    wiki?.click();
    expect(emitted).toEqual(['Alpha']);
  });

  it('highlights only the Wiki row when the active surface is the wiki rail', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.componentRef.setInput('activeProjectSurface', 'wiki');
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const hub = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-hub-Alpha"]');
    const wiki = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-wiki-Alpha"]');

    expect(hub?.classList.contains('tree-row--active')).toBe(false);
    expect(wiki?.classList.contains('tree-row--active')).toBe(true);
    expect(wiki?.getAttribute('aria-current')).toBe('page');
  });

  it('right-click opens a text-only Rename menu that starts the inline rename', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const g = cmp.groups()[0];
    const ev = { preventDefault: noop, clientX: 12, clientY: 34 } as MouseEvent;
    cmp.openWsContextMenu(ev, g);

    expect(cmp.wsContextMenu()).toEqual({ id: 'ws-default', x: 12, y: 34 });
    const items = cmp.wsContextMenuItems();
    expect(items).toEqual([{ kind: 'row', id: 'rename', label: 'Rename' }]);

    cmp.onWsContextMenuItemClick({ id: 'rename', item: items[0] as never });
    expect(cmp.wsContextMenu()).toBeNull();
    expect(cmp.renamingWsId()).toBe('ws-default');
    expect(cmp.renameDraft()).toBe('Default');
  });

  it('does not open a custom context menu for synthetic groups', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const g = cmp.groups()[0]; // synthetic "__all__" fallback
    let prevented = false;
    cmp.openWsContextMenu({ preventDefault: () => { prevented = true; }, clientX: 1, clientY: 2 } as MouseEvent, g);

    expect(prevented).toBe(false);
    expect(cmp.wsContextMenu()).toBeNull();
  });

  it('right-click opens a text-only project menu that starts inline rename', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const alpha = cmp.groups()[0].projects[0];
    let prevented = false;
    let stopped = false;
    cmp.onProjectContextMenu({
      preventDefault: () => { prevented = true; },
      stopPropagation: () => { stopped = true; },
      clientX: 20,
      clientY: 40,
    } as unknown as MouseEvent, alpha);

    expect(prevented).toBe(true);
    expect(stopped).toBe(true);
    expect(cmp.projectActions.contextMenu()).toEqual({
      projectId: 'PROJ-Alpha',
      name: 'Alpha',
      displayName: 'Alpha',
      shortCode: 'ALP',
      x: 20,
      y: 40,
    });
    expect(cmp.projectActions.contextMenuItems()).toEqual([
      { kind: 'row', id: 'rename', label: 'Rename' },
      { kind: 'row', id: 'delete', label: 'Delete project…', danger: true },
    ]);

    cmp.onProjectContextMenuItemClick({
      id: 'rename',
      item: cmp.projectActions.contextMenuItems()[0] as never,
    });

    expect(cmp.projectActions.contextMenu()).toBeNull();
    expect(cmp.projectActions.renamingProjectId()).toBe('PROJ-Alpha');
    expect(cmp.projectActions.renameDraft()).toBe('Alpha');
  });

  it('opens the project menu only from the project row, not its child rows', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const projectRow = root.querySelector<HTMLElement>(
      '[data-testid="studio-explorer-project-Alpha"]',
    );
    const childRow = root.querySelector<HTMLElement>(
      '[data-testid="studio-explorer-project-board-Alpha"]',
    );
    expect(projectRow).not.toBeNull();
    expect(childRow).not.toBeNull();

    const projectEvent = new MouseEvent('contextmenu', {
      bubbles: true,
      cancelable: true,
      clientX: 20,
      clientY: 40,
    });
    projectRow!.dispatchEvent(projectEvent);

    expect(projectEvent.defaultPrevented).toBe(true);
    expect(cmp.projectActions.contextMenu()?.projectId).toBe('PROJ-Alpha');

    cmp.projectActions.closeContextMenu();
    const childEvent = new MouseEvent('contextmenu', {
      bubbles: true,
      cancelable: true,
      clientX: 30,
      clientY: 50,
    });
    childRow!.dispatchEvent(childEvent);

    expect(childEvent.defaultPrevented).toBe(false);
    expect(cmp.projectActions.contextMenu()).toBeNull();
  });

  // AGT-2381: the project menu binding lives on the project's <app-tree-row>,
  // not on the wrapping .studio-tree-project element — the wrapper also contains
  // every destination row, so binding there leaked Rename/Delete project onto
  // the deepest child rows too.
  it('leaves the deepest destination rows (project URLs) to the native menu', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    const alpha = project('Alpha', 'ws-default', '/repos/Alpha');
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [{
        ...alpha,
        urls: [{ id: 'url-1', label: 'Preview', url: 'http://localhost:4200', sortOrder: 0, startRule: null }],
      }]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    for (const testid of [
      'studio-explorer-project-wiki-Alpha',
      'studio-explorer-project-epics-Alpha',
      'studio-explorer-project-url-Alpha-url-1',
    ]) {
      const child = root.querySelector<HTMLElement>(`[data-testid="${testid}"]`);
      expect(child, testid).not.toBeNull();
      const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
      child!.dispatchEvent(event);
      expect(event.defaultPrevented, testid).toBe(false);
      expect(cmp.projectActions.contextMenu(), testid).toBeNull();
    }
  });

  it('leaves an unregistered project row to the native menu', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [workspace('ws-default', 'Default', 0, [])]);
    fixture.componentRef.setInput('projectRows', [row('Ghost')]);
    fixture.detectChanges();

    const ghost = fixture.nativeElement.querySelector('[data-testid="studio-explorer-project-Ghost"]') as HTMLElement;
    expect(ghost).not.toBeNull();
    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
    ghost.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
    expect(cmp.projectActions.contextMenu()).toBeNull();
  });

  it('project delete menu emits stable id plus display name and short code', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const emitted: { projectId: string; displayName: string; shortCode: string | null }[] = [];
    cmp.deleteProject.subscribe(v => emitted.push(v));
    cmp.onProjectContextMenu({
      preventDefault: noop,
      stopPropagation: noop,
      clientX: 1,
      clientY: 2,
    } as unknown as MouseEvent, cmp.groups()[0].projects[0]);

    cmp.onProjectContextMenuItemClick({
      id: 'delete',
      item: cmp.projectActions.contextMenuItems()[1] as never,
    });

    expect(cmp.projectActions.contextMenu()).toBeNull();
    expect(emitted).toEqual([{ projectId: 'PROJ-Alpha', displayName: 'Alpha', shortCode: 'ALP' }]);
  });
});

describe('ExplorerWorkspaceTreeComponent auto-pickup indicator', () => {
  const states = (entries: [string, ProjectAutoPickupState][]) =>
    new Map<string, ProjectAutoPickupIndicator>(entries.map(([name, state]) => [
      name,
      {
        state,
        reason: state === 'blocked' ? 'build profile declared' : null,
        tooltip: state === 'blocked'
          ? 'Auto-pickup blocked: build profile declared'
          : `Auto-pickup ${state}`,
      },
    ]));

  it('renders active, paused, manual, and blocked marks on project rows', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    cmp.setCollapsed('ws:__all__', false);
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta'), row('Gamma'), row('Delta')]);
    fixture.componentRef.setInput('projectAutoPickupByName', states([
      ['Alpha', 'active'],
      ['Beta', 'paused'],
      ['Gamma', 'manual'],
      ['Delta', 'blocked'],
    ]));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const dot = (name: string) =>
      root.querySelector<HTMLElement>(`[data-testid="studio-explorer-project-auto-pickup-${name}"]`);

    expect(dot('Alpha')?.getAttribute('data-auto-pickup-state')).toBe('active');
    expect(dot('Beta')?.getAttribute('data-auto-pickup-state')).toBe('paused');
    expect(dot('Gamma')?.getAttribute('data-auto-pickup-state')).toBe('manual');
    expect(dot('Delta')?.getAttribute('data-auto-pickup-state')).toBe('blocked');
    expect(dot('Delta')?.getAttribute('data-auto-pickup-reason')).toBe('build profile declared');
    expect(dot('Delta')?.getAttribute('aria-label')).toBe('Auto-pickup blocked: build profile declared');
    expect(dot('Alpha')?.closest('.tree-row__glyph-overlay')).not.toBeNull();
    expect(dot('Alpha')?.closest('[aria-hidden="true"]')).toBeNull();
  });

  it('rolls active and blocked auto projects into a blocked-wins aggregate', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [
        project('Alpha', 'ws-default', '/repos/Alpha'),
        project('Beta', 'ws-default', '/repos/Beta'),
        project('Gamma', 'ws-default', '/repos/Gamma'),
      ]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta'), row('Gamma')]);
    fixture.componentRef.setInput('projectAutoPickupByName', states([
      ['Alpha', 'active'],
      ['Beta', 'blocked'],
      ['Gamma', 'manual'],
    ]));

    const agg = cmp.wsAutoPickupAggregate(cmp.groups()[0]);
    expect(agg).toEqual({ state: 'blocked', autoProjects: ['Alpha', 'Beta'] });
    expect(cmp.aggregateAutoPickupTooltip(agg)).toBe('Auto-pickup blocked: Alpha, Beta');
  });

  it('shows the aggregate mark on a collapsed workspace header, hides it when expanded', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    fixture.componentRef.setInput('projectAutoPickupByName', states([['Alpha', 'active']]));

    const root: HTMLElement = fixture.nativeElement;
    const wsDot = () => root.querySelector<HTMLElement>('[data-testid="studio-explorer-ws-auto-pickup-ws-default"]');

    cmp.setCollapsed('ws:ws-default', false);
    fixture.detectChanges();
    expect(wsDot()).toBeNull(); // expanded → per-project dots carry the signal

    cmp.setCollapsed('ws:ws-default', true);
    fixture.detectChanges();
    expect(wsDot()?.getAttribute('data-auto-pickup-state')).toBe('active');
    expect(wsDot()?.getAttribute('aria-label')).toBe('Auto-pickup active: Alpha');
  });

  it('surfaces the whole-tree aggregate on the panel header only when collapsed', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);
    fixture.componentRef.setInput('projectAutoPickupByName', states([
      ['Alpha', 'active'],
      ['Beta', 'blocked'],
    ]));

    expect(cmp.allAutoPickupAggregate()).toEqual({ state: 'blocked', autoProjects: ['Alpha', 'Beta'] });

    const root: HTMLElement = fixture.nativeElement;
    const panelDot = () => root.querySelector<HTMLElement>('[data-testid="studio-explorer-workspace-auto-pickup"]');

    cmp.setCollapsed('workspace', false);
    fixture.detectChanges();
    expect(panelDot()).toBeNull();

    cmp.setCollapsed('workspace', true);
    fixture.detectChanges();
    expect(panelDot()?.getAttribute('data-auto-pickup-state')).toBe('blocked');
  });
});

describe('ExplorerWorkspaceTreeComponent — project drag-and-drop (F46 workspace reassignment)', () => {
  function twoWorkspaces(cmp: ExplorerWorkspaceTreeComponent, fixture: ReturnType<typeof mount>) {
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
      workspace('ws-2', 'Side Projects', 1, [project('Beta', 'ws-2', '/repos/Beta')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta'), row('Orphan')]);
    cmp.projectDrag.reset();
  }

  const dropEvent = (projectNode: ReturnType<ExplorerWorkspaceTreeComponent['groups']>[number]['projects'][number]) =>
    ({ item: { data: projectNode } } as unknown as Parameters<ExplorerWorkspaceTreeComponent['onWorkspaceDrop']>[0]);

  it('attaches the registry projectId + owning workspaceId to matched nodes', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const groups = cmp.groups();
    const alpha = groups[0].projects[0];
    expect(alpha.projectId).toBe('PROJ-Alpha');
    expect(alpha.workspaceId).toBe('ws-default');

    const orphan = groups.find(g => g.id === '__unassigned__')!.projects[0];
    expect(orphan.projectId).toBeNull();
    expect(orphan.workspaceId).toBeNull();
  });

  it('drops a project onto a different workspace and emits the reassignment', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const groups = cmp.groups();
    const alpha = groups[0].projects[0]; // lives in ws-default
    const sideGroup = groups[1];         // ws-2

    cmp.onDragStart(alpha);
    expect(cmp.projectDrag.draggingProjectId()).toBe('PROJ-Alpha');
    expect(cmp.canDropOnWorkspace('ws-2')).toBe(true);
    expect(cmp.canDropOnWorkspace('ws-default')).toBe(false); // same workspace = no-op

    let emitted: { projectId: string; targetWorkspaceId: string } | null = null;
    const sub = cmp.projectDrop.subscribe(e => (emitted = e));
    cmp.onWorkspaceDrop(dropEvent(alpha), sideGroup);
    sub.unsubscribe();

    expect(emitted).toEqual({ projectId: 'PROJ-Alpha', targetWorkspaceId: 'ws-2' });
    // Drag state is cleared on drop.
    expect(cmp.projectDrag.draggingProjectId()).toBeNull();
  });

  it('does not emit when a project is dropped on its own workspace', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const groups = cmp.groups();
    const alpha = groups[0].projects[0]; // ws-default
    cmp.onDragStart(alpha);

    let emitted = false;
    const sub = cmp.projectDrop.subscribe(() => (emitted = true));
    cmp.onWorkspaceDrop(dropEvent(alpha), groups[0]);
    sub.unsubscribe();

    expect(emitted).toBe(false);
  });

  it('refuses to start a drag for an unregistered (__unassigned__) row', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const orphan = cmp.groups().find(g => g.id === '__unassigned__')!.projects[0];
    cmp.onDragStart(orphan);
    expect(cmp.projectDrag.draggingProjectId()).toBeNull();
  });

  it('rejects synthetic workspaces as drop targets even mid-drag', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const alpha = cmp.groups()[0].projects[0];
    cmp.onDragStart(alpha);
    expect(cmp.canDropOnWorkspace('__unassigned__')).toBe(false);
    expect(cmp.canDropOnWorkspace('__all__')).toBe(false);
  });
});

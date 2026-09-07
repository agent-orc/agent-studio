import { TestBed } from '@angular/core/testing';
import { StudioTabStateService } from './studio-tab-state.service';
import type { StudioTab } from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';
import { ensureBrowserStorage } from '../../../../testing/browser-storage';

const STORAGE_KEY = 'atp.studio.tabs.v1';
const ALL_BOARD_KEY = 'board:__all__';

ensureBrowserStorage();

describe('StudioTabStateService', () => {
  let svc: StudioTabStateService;

  beforeEach(() => {
    localStorage.removeItem(STORAGE_KEY);
    TestBed.configureTestingModule({ providers: [StudioTabStateService] });
    svc = TestBed.inject(StudioTabStateService);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(STORAGE_KEY);
  });

  describe('default All-projects board tab (closable)', () => {
    it('seeds the board:__all__ tab at boot when storage is empty', () => {
      expect(svc.tabs()).toHaveLength(1);
      const board = svc.tabs()[0];
      expect(studioTabKey(board)).toBe(ALL_BOARD_KEY);
      expect(board.kind).toBe('board');
      // It is a plain tab now — no sticky marker.
      expect((board as { sticky?: boolean }).sticky).toBeUndefined();
      expect(svc.activeKey()).toBe(ALL_BOARD_KEY);
    });

    it('is closable like any other tab, leaving the empty-state', () => {
      svc.close(ALL_BOARD_KEY);
      expect(svc.tabs()).toHaveLength(0);
      expect(svc.activeKey()).toBeNull();
      expect(svc.activeTab()).toBeNull();
    });

    it('activateAllProjectsBoard() re-opens and focuses the board from the empty-state', () => {
      svc.closeAll();
      expect(svc.tabs()).toHaveLength(0);
      const key = svc.activateAllProjectsBoard();
      expect(key).toBe(ALL_BOARD_KEY);
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY]);
      expect(svc.activeKey()).toBe(ALL_BOARD_KEY);
    });

    it('activateAllProjectsBoard() focuses an already-open board without duplicating', () => {
      svc.open({ kind: 'task', taskKey: 'a' });
      expect(svc.activeKey()).toBe('task:a');
      svc.activateAllProjectsBoard();
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY, 'task:a']);
      expect(svc.activeKey()).toBe(ALL_BOARD_KEY);
    });
  });

  it('opens a tab and makes it active', () => {
    const tab: StudioTab = { kind: 'board', projectName: 'demo' };
    svc.open(tab);
    expect(svc.tabs()).toContainEqual(tab);
    expect(svc.activeKey()).toBe('board:demo');
    expect(svc.activeTab()).toEqual(tab);
  });

  it('signals the S5 entry only for a project surface opened from an empty editor', () => {
    svc.closeAll();
    svc.open({ kind: 'task', taskKey: 'task-a' });
    expect(svc.emptyProjectEntryRevision()).toBe(0);

    svc.closeAll();
    svc.open({ kind: 'board', projectName: 'Alpha' });
    expect(svc.emptyProjectEntryRevision()).toBe(1);
    svc.open({ kind: 'hub', projectName: 'Beta', section: 'overview' });
    expect(svc.emptyProjectEntryRevision()).toBe(1);
  });

  it('keeps the workspace Feed as one persistent editor tab', () => {
    const tab: StudioTab = { kind: 'feed' };
    svc.open(tab);
    svc.open(tab);

    expect(svc.tabs().filter(item => item.kind === 'feed')).toEqual([tab]);
    expect(svc.activeKey()).toBe('feed');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [StudioTabStateService] });
    expect(TestBed.inject(StudioTabStateService).activeTab()).toEqual(tab);
  });

  it('keeps Chat History as one persistent workspace tab', () => {
    const tab: StudioTab = { kind: 'chat-history' };
    svc.open(tab);
    svc.open(tab);

    expect(svc.tabs().filter(item => item.kind === 'chat-history')).toEqual([tab]);
    expect(svc.activeKey()).toBe('chat-history');
  });

  it('retargets open project tabs and preserves the active settings tab after rename', () => {
    svc.open({ kind: 'board', projectName: 'Old Name' });
    svc.open({ kind: 'hub', projectName: 'Old Name', section: 'settings' });
    svc.open({ kind: 'url-preview', projectName: 'Old Name', urlId: 'dev' });
    svc.select('hub:Old Name');

    svc.renameProject('Old Name', 'New Name');

    expect(svc.tabs()).toContainEqual({ kind: 'board', projectName: 'New Name' });
    expect(svc.tabs()).toContainEqual({ kind: 'hub', projectName: 'New Name', section: 'settings' });
    expect(svc.tabs()).toContainEqual({ kind: 'url-preview', projectName: 'New Name', urlId: 'dev' });
    expect(svc.activeKey()).toBe('hub:New Name');
  });

  it('opens workspace settings as a persistent editor tab', () => {
    const tab: StudioTab = { kind: 'workspace-settings' };
    svc.open(tab);
    expect(studioTabKey(tab)).toBe('workspace-settings');
    expect(svc.activeKey()).toBe('workspace-settings');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [StudioTabStateService] });
    const restored = TestBed.inject(StudioTabStateService);

    expect(restored.activeKey()).toBe('workspace-settings');
    expect(restored.activeTab()).toEqual(tab);
  });

  it('focuses an already-open tab instead of duplicating', () => {
    const tab: StudioTab = { kind: 'task', taskKey: 'demo|x' };
    svc.open(tab);
    svc.open(tab);
    // Default board + 1 task = 2.
    expect(svc.tabs()).toHaveLength(2);
    expect(svc.activeKey()).toBe('task:demo|x');
  });

  describe('Deck and Wiki tab identity', () => {
    it('keeps an open Overview Hub and Explorer-opened Wiki as distinct tabs', () => {
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'overview' });
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'wiki' });

      expect(svc.tabs().filter(t => studioTabKey(t) === 'hub:Project A')).toHaveLength(1);
      expect(svc.tabs().filter(t => studioTabKey(t) === 'hub:Project A:wiki')).toHaveLength(1);
      expect(svc.activeKey()).toBe('hub:Project A:wiki');
      expect(svc.activeTab()).toEqual({ kind: 'hub', projectName: 'Project A', section: 'wiki' });
    });

    it('keys Wiki pages by project and normalized repository path', () => {
      svc.open({
        kind: 'hub',
        projectName: 'Project A',
        section: 'wiki',
        wikiTarget: { kind: 'page', relPath: 'docs/concepts/routing.md' },
      });
      svc.open({
        kind: 'hub',
        projectName: 'Project A',
        section: 'wiki',
        wikiTarget: { kind: 'page', relPath: 'concepts/routing.md' },
      });

      expect(svc.tabs().filter(t => studioTabKey(t)
        === 'hub:Project A:wiki:page:concepts%2Frouting.md')).toHaveLength(1);
      expect(svc.activeTab()).toEqual({
        kind: 'hub',
        projectName: 'Project A',
        section: 'wiki',
        wikiTarget: { kind: 'page', relPath: 'concepts/routing.md' },
      });
    });

    it('keeps different Wiki paths as distinct internal destinations', () => {
      svc.open({
        kind: 'hub', projectName: 'Project A', section: 'wiki',
        wikiTarget: { kind: 'page', relPath: 'concepts/a.md' },
      });
      svc.open({
        kind: 'hub', projectName: 'Project A', section: 'wiki',
        wikiTarget: { kind: 'page', relPath: 'concepts/b.md' },
      });

      expect(svc.tabs().filter(tab => tab.kind === 'hub' && tab.section === 'wiki')).toHaveLength(2);
      expect(svc.activeKey()).toBe('hub:Project A:wiki:page:concepts%2Fb.md');
    });

    it('still adopts fresh sections that belong to the base Hub identity', () => {
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'overview' });
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'drift' });

      expect(svc.tabs().filter(t => studioTabKey(t) === 'hub:Project A')).toHaveLength(1);
      expect(svc.activeTab()).toEqual({ kind: 'hub', projectName: 'Project A', section: 'drift' });
    });

    it('preserves an exact pipeline-row deep link when adopting the Hub tab', () => {
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'overview' });
      svc.open({
        kind: 'hub',
        projectName: 'Project A',
        section: 'pipeline',
        pipelineStepId: 'post-wiki-learnings',
      });

      expect(svc.activeTab()).toEqual({
        kind: 'hub',
        projectName: 'Project A',
        section: 'pipeline',
        pipelineStepId: 'post-wiki-learnings',
      });
    });

    it('keeps the tab in its original slot when adopting a new section', () => {
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'drift' });
      svc.open({ kind: 'task', taskKey: 'later' });
      svc.open({ kind: 'hub', projectName: 'Project A', section: 'overview' });

      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual([ALL_BOARD_KEY, 'hub:Project A', 'task:later']);
    });
  });

  it('closing the active tab returns to the most recently active open tab', () => {
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.open({ kind: 'task', taskKey: 'c' });
    svc.select('task:a');
    svc.select('task:c');

    svc.close('task:c');

    expect(svc.tabs()).toHaveLength(3); // board + task:a + task:b
    expect(svc.activeKey()).toBe('task:a');
  });

  it('falls back to the former last-tab behavior when no MRU survivor exists', () => {
    TestBed.resetTestingModule();
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({
        v: 1,
        tabs: [
          { kind: 'task', taskKey: 'a' },
          { kind: 'task', taskKey: 'b' },
        ],
        activeKey: 'task:a',
      }),
    );
    TestBed.configureTestingModule({ providers: [StudioTabStateService] });
    const restored = TestBed.inject(StudioTabStateService);
    restored.close('task:a');

    expect(restored.activeKey()).toBe('task:b');
  });

  it('closing every tab leaves the empty-state (no active tab)', () => {
    svc.open({ kind: 'task', taskKey: 'only' });
    svc.close('task:only');
    svc.close(ALL_BOARD_KEY);
    expect(svc.tabs()).toHaveLength(0);
    expect(svc.activeKey()).toBeNull();
    expect(svc.activeTab()).toBeNull();
  });

  it('closeOthers keeps only the named tab', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeOthers('task:a');
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['task:a']);
    expect(svc.activeKey()).toBe('task:a');
  });

  it('closeRight removes everything strictly after the anchor', () => {
    // Default board at index 0, then board:demo, task:a, task:b.
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeRight('task:a');
    expect(svc.tabs().map(t => studioTabKey(t)))
      .toEqual([ALL_BOARD_KEY, 'board:demo', 'task:a']);
  });

  it('closeLeft removes everything strictly before the anchor', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeLeft('task:a');
    expect(svc.tabs().map(t => studioTabKey(t)))
      .toEqual(['task:a', 'task:b']);
  });

  it('closeAll empties the tab list and clears the active key', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.closeAll();
    expect(svc.tabs()).toHaveLength(0);
    expect(svc.activeKey()).toBeNull();
  });

  describe('epics tab is a normal, closable tab', () => {
    it('opens the workspace-wide Epics view as an active tab', () => {
      svc.open({ kind: 'epics', projectName: null });
      const key = 'epics:__all__';
      expect(svc.activeKey()).toBe(key);
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY, key]);
    });

    it('close() removes the Epics tab and falls back to the board', () => {
      svc.open({ kind: 'epics', projectName: null });
      svc.close('epics:__all__');
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY]);
      expect(svc.activeKey()).toBe(ALL_BOARD_KEY);
    });
  });

  describe('epic detail tabs', () => {
    it('keys epic detail tabs by epic task key', () => {
      const tab: StudioTab = { kind: 'epic', epicKey: 'watch::epic-a' };
      expect(studioTabKey(tab)).toBe('epic:watch::epic-a');
    });

    it('opens one epic detail tab and preserves its optional viewed task anchor', () => {
      svc.open({ kind: 'epic', epicKey: 'watch::epic-a', viewTaskKey: 'watch::task-a' });

      expect(svc.activeKey()).toBe('epic:watch::epic-a');
      expect(svc.tabs()).toContainEqual({
        kind: 'epic',
        epicKey: 'watch::epic-a',
        viewTaskKey: 'watch::task-a',
      });
    });

    it('normalizes empty epic task anchors out of persisted tabs', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'epic', epicKey: 'watch::epic-a', viewTaskKey: '' },
          ],
          activeKey: 'epic:watch::epic-a',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);

      expect(restored.tabs()).toEqual([{ kind: 'epic', epicKey: 'watch::epic-a', viewTaskKey: undefined }]);
      expect(restored.activeKey()).toBe('epic:watch::epic-a');
    });

    it('dedupes persisted epic detail tabs by epic key', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'epic', epicKey: 'watch::epic-a' },
            { kind: 'epic', epicKey: 'watch::epic-a', viewTaskKey: 'watch::task-a' },
          ],
          activeKey: 'epic:watch::epic-a',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);

      expect(restored.tabs()).toEqual([{ kind: 'epic', epicKey: 'watch::epic-a', viewTaskKey: undefined }]);
      expect(restored.activeKey()).toBe('epic:watch::epic-a');
    });

    it('retargets a task tab into a task-anchored epic tab in place', () => {
      svc.open({ kind: 'task', taskKey: 'watch::task-a' });
      svc.retarget('task:watch::task-a', {
        kind: 'epic',
        epicKey: 'watch::epic-a',
        viewTaskKey: 'watch::task-a',
      });

      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY, 'epic:watch::epic-a']);
      expect(svc.tabs()[1]).toEqual({
        kind: 'epic',
        epicKey: 'watch::epic-a',
        viewTaskKey: 'watch::task-a',
      });
      expect(svc.activeKey()).toBe('epic:watch::epic-a');
    });

    it('retarget focuses an existing target tab instead of duplicating it', () => {
      svc.open({ kind: 'epic', epicKey: 'watch::epic-a' });
      svc.open({ kind: 'task', taskKey: 'watch::task-a' });
      svc.retarget('task:watch::task-a', {
        kind: 'epic',
        epicKey: 'watch::epic-a',
        viewTaskKey: 'watch::task-a',
      });

      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY, 'epic:watch::epic-a']);
      expect(svc.tabs()[1]).toEqual({
        kind: 'epic',
        epicKey: 'watch::epic-a',
        viewTaskKey: 'watch::task-a',
      });
      expect(svc.activeKey()).toBe('epic:watch::epic-a');
    });
  });

  describe('move (drag-reorder)', () => {
    function seed(): void {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', taskKey: 'a' });
      svc.open({ kind: 'task', taskKey: 'b' });
      svc.open({ kind: 'task', taskKey: 'c' });
      // Layout: [board, board:demo, task:a, task:b, task:c]. Active = task:c.
    }

    it('moves a tab to land before the target', () => {
      seed();
      svc.move('task:c', 'task:a');
      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual([ALL_BOARD_KEY, 'board:demo', 'task:c', 'task:a', 'task:b']);
    });

    it('moves a tab to the end when target is null', () => {
      seed();
      svc.move('board:demo', null);
      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual([ALL_BOARD_KEY, 'task:a', 'task:b', 'task:c', 'board:demo']);
    });

    it('can reorder the default board tab now that it is not pinned', () => {
      seed();
      svc.move(ALL_BOARD_KEY, null);
      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual(['board:demo', 'task:a', 'task:b', 'task:c', ALL_BOARD_KEY]);
    });

    it('keeps the active key stable across a move', () => {
      seed();
      expect(svc.activeKey()).toBe('task:c');
      svc.move('task:a', 'task:c');
      expect(svc.activeKey()).toBe('task:c');
    });

    it('is a no-op when source and target match', () => {
      seed();
      const before = svc.tabs().map(t => studioTabKey(t));
      svc.move('task:b', 'task:b');
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(before);
    });

    it('is a no-op when the source key does not exist', () => {
      seed();
      const before = svc.tabs().map(t => studioTabKey(t));
      svc.move('task:does-not-exist', 'task:a');
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(before);
    });
  });

  describe('persistence across reloads', () => {
    it('writes the current state to localStorage on every change', () => {
      svc.open({ kind: 'task', taskKey: 'a' });
      const raw = localStorage.getItem(STORAGE_KEY);
      expect(raw).not.toBeNull();
      const parsed = JSON.parse(raw!);
      expect(parsed.v).toBe(1);
      // board + task:a = 2.
      expect(parsed.tabs).toHaveLength(2);
      expect(parsed.activeKey).toBe('task:a');
    });

    it('restores tabs + active key when constructed against a previously-written payload', () => {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', taskKey: 'a' });
      svc.open({ kind: 'task', taskKey: 'b' });
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t)))
        .toEqual([ALL_BOARD_KEY, 'board:demo', 'task:a', 'task:b']);
      expect(restored.activeKey()).toBe('task:b');
    });

    it('restores the All-projects board as the active reload surface', () => {
      svc.open({ kind: 'task', taskKey: 'a' });
      svc.select(ALL_BOARD_KEY);
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);

      expect(restored.activeKey()).toBe(ALL_BOARD_KEY);
      expect(restored.activeTab()).toEqual({ kind: 'board', projectName: '__all__' });
    });

    it('restores a project board as the active reload surface', () => {
      svc.open({ kind: 'board', projectName: 'Project A' });
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);

      expect(restored.activeKey()).toBe('board:Project A');
      expect(restored.activeTab()).toEqual({ kind: 'board', projectName: 'Project A' });
    });

    it('drops persisted Backlog Triage tabs and falls back to a surviving tab', () => {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__' },
          { kind: 'backlog', projectName: 'Project A' },
        ],
        activeKey: 'backlog:Project A',
      }));
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);

      expect(restored.tabs()).toEqual([{ kind: 'board', projectName: '__all__' }]);
      expect(restored.activeKey()).toBe(ALL_BOARD_KEY);
      expect(restored.activeTab()).toEqual({ kind: 'board', projectName: '__all__' });
      expect(localStorage.getItem(STORAGE_KEY)).not.toContain('backlog');
    });

    it('restores a project navigation tab as the active reload surface', () => {
      svc.open({ kind: 'hub', projectName: 'Project A' });
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);

      expect(restored.activeKey()).toBe('hub:Project A');
      expect(restored.activeTab()).toEqual({ kind: 'hub', projectName: 'Project A', section: undefined });
    });

    it('honours a persisted empty tab list (user closed everything) and shows the empty-state', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ v: 1, tabs: [], activeKey: null }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toHaveLength(0);
      expect(restored.activeKey()).toBeNull();
    });

    it('drops a version-mismatch payload and re-seeds the default board', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ v: 99, tabs: [{ kind: 'task', taskKey: 'z' }], activeKey: 'task:z' }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY]);
      expect(restored.activeKey()).toBe(ALL_BOARD_KEY);
    });

    it('falls back to the trailing tab when the persisted activeKey is gone', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'board', projectName: '__all__' },
            { kind: 'board', projectName: 'demo' },
            { kind: 'task', taskKey: 'a' },
          ],
          activeKey: 'task:missing',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toHaveLength(3);
      expect(restored.activeKey()).toBe('task:a');
    });

    it('migrates a legacy sticky:true snapshot into a plain board tab', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'board', projectName: '__all__', sticky: true },
            { kind: 'task', taskKey: 'a' },
          ],
          activeKey: 'task:a',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toHaveLength(2);
      const board = restored.tabs().find(t => studioTabKey(t) === ALL_BOARD_KEY)!;
      expect((board as { sticky?: boolean }).sticky).toBeUndefined();
      expect(restored.activeKey()).toBe('task:a');
    });

    it('survives a corrupt payload without throwing and re-seeds the default board', () => {
      localStorage.setItem(STORAGE_KEY, 'not-json{');
      expect(() => {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: [StudioTabStateService] });
        TestBed.inject(StudioTabStateService);
      }).not.toThrow();
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY]);
    });
  });

  /**
   * AGT-2692 — a task tab records the scope it was opened in, so the app
   * never switches projects just because a card was clicked. The task's own
   * project remains the detail view's data source and is untouched here.
   */
  describe('task tab origin scope', () => {
    const scopeOf = (svc: StudioTabStateService, key: string) => {
      const tab = svc.tabs().find(t => studioTabKey(t) === key);
      return tab?.kind === 'task' ? tab.scope : undefined;
    };

    it('stamps all-projects when the task is opened from the cross-project board', () => {
      expect(svc.activeKey()).toBe(ALL_BOARD_KEY);
      svc.open({ kind: 'task', taskKey: 'w::a' });
      expect(scopeOf(svc, 'task:w::a')).toEqual({ kind: 'all-projects' });
    });

    it('stamps the project when the task is opened from that project board', () => {
      svc.open({ kind: 'board', projectName: 'Alpha' });
      svc.open({ kind: 'task', taskKey: 'w::a' });
      expect(scopeOf(svc, 'task:w::a')).toEqual({ kind: 'project', projectName: 'Alpha' });
    });

    it('keeps the original stamp when the same task is re-opened from elsewhere', () => {
      svc.open({ kind: 'task', taskKey: 'w::a' });
      svc.open({ kind: 'board', projectName: 'Alpha' });
      svc.open({ kind: 'task', taskKey: 'w::a' });
      expect(svc.activeKey()).toBe('task:w::a');
      expect(scopeOf(svc, 'task:w::a')).toEqual({ kind: 'all-projects' });
    });

    it('lets an explicit scope from the caller win over the origin', () => {
      svc.open({ kind: 'board', projectName: 'Alpha' });
      svc.open({ kind: 'task', taskKey: 'w::a', scope: { kind: 'all-projects' } });
      expect(scopeOf(svc, 'task:w::a')).toEqual({ kind: 'all-projects' });
    });

    it('carries the scope through a pager retarget so walking a lane never drifts', () => {
      svc.open({ kind: 'task', taskKey: 'w::a' });
      svc.retarget('task:w::a', { kind: 'task', taskKey: 'w::b' });
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual([ALL_BOARD_KEY, 'task:w::b']);
      expect(scopeOf(svc, 'task:w::b')).toEqual({ kind: 'all-projects' });
    });

    it('persists the stamp across a reload', () => {
      svc.open({ kind: 'task', taskKey: 'w::a' });
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(scopeOf(restored, 'task:w::a')).toEqual({ kind: 'all-projects' });
    });

    it('drops an unusable persisted stamp instead of scoping to an empty project', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [{ kind: 'task', taskKey: 'w::a', scope: { kind: 'project', projectName: '' } }],
          activeKey: 'task:w::a',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(scopeOf(restored, 'task:w::a')).toBeUndefined();
    });

    it('leaves the task tab unstamped when the origin carries no project context', () => {
      svc.open({ kind: 'diff', commitSha: 'abc' });
      svc.open({ kind: 'task', taskKey: 'w::a' });
      expect(scopeOf(svc, 'task:w::a')).toBeUndefined();
    });
  });
});

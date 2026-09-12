import { Injectable, computed, signal } from '@angular/core';
import type {
  BoardTab,
  DocumentTabHistory,
  DocumentTabTarget,
  StudioTab,
  StudioTabReusePolicy,
} from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';
import {
  ALL_PROJECTS_BOARD_NAME,
  normalizeTaskTabScope,
  resolveTaskTabScope,
} from './task-tab-scope';

const STORAGE_KEY = 'atp.studio.tabs.v1';
const STORAGE_VERSION = 1;
const ALL_PROJECTS = ALL_PROJECTS_BOARD_NAME;
const ALL_BOARD_TAB: BoardTab = { kind: 'board', projectName: ALL_PROJECTS };
const ALL_BOARD_KEY = studioTabKey(ALL_BOARD_TAB);
const DOCUMENT_HISTORY_LIMIT = 50;

interface PersistedState {
  v: number;
  tabs: StudioTab[];
  activeKey: string | null;
}

/**
 * Owns the open-editor-tab list and the active tab for the studio shell.
 *
 * Behaviour mirrors the VS-Code editor surface: opening a tab that is
 * already present focuses it instead of duplicating; closing the active
 * tab returns to the most recently active tab that is still open. When that
 * in-memory history has no survivor, the last remaining tab is the fallback.
 * When the list goes empty the editor surface shows the creative idle
 * empty-state (no tab is active). Drag-reorder is supported through
 * {@link move}.
 *
 * Every tab is a first-class, closable tab - including the cross-project
 * "All projects" board (`board:__all__`). On a fresh boot (no persisted
 * snapshot) that board is seeded so first-run users land somewhere useful;
 * once the user closes it, the choice persists and the empty-state shows.
 *
 * State is persisted to <code>localStorage</code> under the versioned
 * key {@link STORAGE_KEY} so the editor surface looks the same after a
 * reload. The version prefix lets us evolve the tab shape without
 * silently breaking older snapshots; a payload with an unknown version
 * is dropped (and the default board re-seeded) rather than crashing boot.
 */
@Injectable({ providedIn: 'root' })
export class StudioTabStateService {
  private readonly _tabs = signal<StudioTab[]>([]);
  private readonly _activeKey = signal<string | null>(null);
  private readonly _emptyProjectEntryRevision = signal(0);
  /** Session-only least-recent to most-recent activation order. */
  private activationHistory: string[] = [];

  readonly tabs = this._tabs.asReadonly();
  readonly activeKey = this._activeKey.asReadonly();
  /** Advances only when an explicit project surface opens from an empty editor. */
  readonly emptyProjectEntryRevision = this._emptyProjectEntryRevision.asReadonly();

  readonly activeTab = computed<StudioTab | null>(() => {
    const key = this._activeKey();
    if (!key) return null;
    return this._tabs().find(t => studioTabKey(t) === key) ?? null;
  });
  readonly canNavigateDocumentBack = computed(() => this.documentHistoryOffset(-1) !== null);
  readonly canNavigateDocumentForward = computed(() => this.documentHistoryOffset(1) !== null);
  readonly activeDocumentHistoryLength = computed(() => {
    const tab = this.activeTab();
    return this.isDocumentTab(tab) ? (tab.documentHistory?.entries.length ?? 1) : 0;
  });

  constructor() {
    const hadSnapshot = this.restore();
    if (!hadSnapshot) {
      // Fresh boot / dropped payload: seed the cross-project board so the
      // user isn't dropped straight onto the empty-state on first run.
      this._tabs.set([ALL_BOARD_TAB]);
      this._activeKey.set(ALL_BOARD_KEY);
    }
    this.recordActivation(this._activeKey());
    this.persist();
  }

  /** Open (creating if missing) and focus the cross-project All-projects board. */
  activateAllProjectsBoard(): string {
    this.open(ALL_BOARD_TAB);
    return ALL_BOARD_KEY;
  }

  /**
   * Open the tab if it's not already there, then focus it. When a tab with
   * the same key is already open, adopt the fresh payload in place rather
   * than keeping the stale one — the tab key intentionally omits some fields
   * (a Deck tab keys on project only, not its {@link HubTab.section}), so an
   * `open()` that carries a new section must be able to move the open tab to
   * it. Without this, re-opening the Deck on a different section (Project vs.
   * Wiki) would silently drop the section and "do nothing".
   *
   * A brand-new task tab is stamped with the scope it is opened from
   * (AGT-2692) so the app-wide project selection follows the context the user
   * navigated from, not the task's own project.
   */
  open(tab: StudioTab, reuse: StudioTabReusePolicy = 'new'): void {
    const normalized = this.stampTaskTabScope(this.normalizeTab(tab));
    const active = this.activeTab();
    if (reuse === 'replace-current' && this.isDocumentTab(active) && this.isDocumentTab(normalized)) {
      this.replaceActiveDocument(active, normalized);
      return;
    }
    const emptyProjectEntry = this._tabs().length === 0
      && ((normalized.kind === 'board' && normalized.projectName !== ALL_PROJECTS)
        || normalized.kind === 'hub');
    const key = studioTabKey(normalized);
    this._tabs.update(list => {
      const idx = list.findIndex(t => studioTabKey(t) === key);
      if (idx < 0) return [...list, normalized];
      const next = list.slice();
      next[idx] = normalized;
      return next;
    });
    this.activate(key);
    this.persist();
    if (emptyProjectEntry) this._emptyProjectEntryRevision.update(revision => revision + 1);
  }

  /** Walk the active Wiki/Dossier tab's bounded history without adding an entry. */
  navigateDocumentHistory(delta: -1 | 1): boolean {
    const active = this.activeTab();
    if (!this.isDocumentTab(active)) return false;
    const history = active.documentHistory ?? this.initialDocumentHistory(active);
    const index = history.index + delta;
    if (index < 0 || index >= history.entries.length) return false;
    const nextHistory = { entries: history.entries, index };
    const next = this.tabFromDocumentTarget(history.entries[index], active.documentId, nextHistory);
    this.retarget(studioTabKey(active), next);
    return true;
  }

  /** Reconcile a browser-history route with the nearest entry in the active document tab. */
  restoreDocumentRoute(tab: StudioTab): void {
    const active = this.activeTab();
    const normalized = this.normalizeTab(tab);
    if (!this.isDocumentTab(active) || !this.isDocumentTab(normalized)) {
      this.open(tab, 'new');
      return;
    }
    const history = active.documentHistory ?? this.initialDocumentHistory(active);
    const target = this.documentTarget(normalized);
    const adjacent = [history.index - 1, history.index + 1]
      .find(index => index >= 0 && index < history.entries.length
        && this.sameDocumentTarget(history.entries[index], target));
    if (adjacent !== undefined) {
      const nextHistory = { entries: history.entries, index: adjacent };
      this.retarget(studioTabKey(active), this.tabFromDocumentTarget(target, active.documentId, nextHistory));
      return;
    }
    this.replaceActiveDocument(active, normalized);
  }

  /** Focus an existing tab by key. No-op when the key is unknown. */
  select(key: string): void {
    if (this._tabs().some(t => studioTabKey(t) === key)) {
      this.activate(key);
      this.persist();
    }
  }

  /**
   * Replace an existing tab in place and focus the replacement. If another
   * tab already owns the replacement key, update that target payload, remove
   * the source, and focus the target instead of duplicating.
   */
  retarget(sourceKey: string, tab: StudioTab): void {
    const list = this._tabs();
    const sourceIdx = list.findIndex(t => studioTabKey(t) === sourceKey);
    if (sourceIdx < 0) {
      this.open(this.normalizeTab(tab));
      return;
    }
    // Reusing a tab is one continuous navigation, so an unstamped replacement
    // keeps the scope the tab it replaces was opened in (AGT-2692).
    let normalized = this.carryTaskTabScope(this.normalizeTab(tab), list[sourceIdx]);
    normalized = this.carryDocumentState(normalized, list[sourceIdx]);
    const targetKey = studioTabKey(normalized);
    const existingIdx = list.findIndex((t, i) => i !== sourceIdx && studioTabKey(t) === targetKey);
    if (existingIdx >= 0) {
      const adopted = this.carryTaskTabScope(normalized, list[existingIdx]);
      this._tabs.set(
        list
          .map((t, i) => i === existingIdx ? adopted : t)
          .filter((_, i) => i !== sourceIdx),
      );
      this.activationHistory = this.activationHistory.filter(key => key !== sourceKey);
      this.activate(targetKey);
      this.persist();
      return;
    }
    const next = list.slice();
    next[sourceIdx] = normalized;
    this._tabs.set(next);
    this.activationHistory = this.activationHistory.map(key => key === sourceKey ? targetKey : key);
    this.activate(targetKey);
    this.persist();
  }

  /** Close the tab; an active close returns to the MRU open tab. */
  close(key: string): void {
    const next = this._tabs().filter(t => studioTabKey(t) !== key);
    const wasActive = this._activeKey() === key;
    this._tabs.set(next);
    this.activationHistory = this.activationHistory.filter(item => item !== key);
    if (wasActive) this.activateMostRecent(next, this.lastKey(next));
    else this.reconcileActivationHistory(next);
    this.persist();
  }

  /** Close every tab except the one given. */
  closeOthers(keepKey: string): void {
    const next = this._tabs().filter(t => studioTabKey(t) === keepKey);
    this._tabs.set(next);
    this.reconcileActivationHistory(next);
    if (next.length) this.activate(keepKey);
    else this._activeKey.set(null);
    this.persist();
  }

  /** Close every tab whose index is strictly greater than the anchor's. */
  closeRight(anchorKey: string): void {
    const list = this._tabs();
    const idx = list.findIndex(t => studioTabKey(t) === anchorKey);
    if (idx < 0) return;
    const next = list.filter((_, i) => i <= idx);
    const activeSurvives = next.some(t => studioTabKey(t) === this._activeKey());
    this._tabs.set(next);
    this.reconcileActivationHistory(next);
    if (!activeSurvives) this.activateMostRecent(next, anchorKey);
    this.persist();
  }

  /** Close every tab whose index is strictly less than the anchor's. */
  closeLeft(anchorKey: string): void {
    const list = this._tabs();
    const idx = list.findIndex(t => studioTabKey(t) === anchorKey);
    if (idx < 0) return;
    const next = list.filter((_, i) => i >= idx);
    const activeSurvives = next.some(t => studioTabKey(t) === this._activeKey());
    this._tabs.set(next);
    this.reconcileActivationHistory(next);
    if (!activeSurvives) this.activateMostRecent(next, anchorKey);
    this.persist();
  }

  /** Close every tab. The editor surface falls back to the empty-state. */
  closeAll(): void {
    this._tabs.set([]);
    this._activeKey.set(null);
    this.activationHistory = [];
    this.persist();
  }

  /**
   * Reorder: move the tab with <code>sourceKey</code> so it lands in the
   * slot <em>before</em> the tab with <code>targetKey</code>. Drop on the
   * source itself is a no-op. To move a tab to the very end, pass
   * <code>targetKey = null</code>.
   *
   * The active tab key never changes — moving the active tab keeps it
   * focused; moving a non-active tab leaves the focus where it was.
   */
  move(sourceKey: string, targetKey: string | null): void {
    const list = this._tabs();
    const fromIdx = list.findIndex(t => studioTabKey(t) === sourceKey);
    if (fromIdx < 0) return;
    if (sourceKey === targetKey) return;

    const next = list.slice();
    const [moved] = next.splice(fromIdx, 1);

    if (targetKey === null) {
      next.push(moved);
    } else {
      // After removal the target's index may have shifted left by 1.
      const targetIdx = next.findIndex(t => studioTabKey(t) === targetKey);
      if (targetIdx < 0) {
        // Target vanished mid-flight (rare race). Put it back where it was.
        next.splice(fromIdx, 0, moved);
        return;
      }
      next.splice(targetIdx, 0, moved);
    }
    this._tabs.set(next);
    this.persist();
  }

  /**
   * Remove board and hub tabs whose projectName is no longer in the
   * registry. Called once after the watch-path data arrives so tabs
   * persisted under a pre-rename name don't produce empty boards.
   */
  purgeStaleProjectTabs(validNames: ReadonlySet<string>): void {
    const before = this._tabs();
    const after = before.filter(t => {
      if (t.kind === 'board' && t.projectName !== ALL_PROJECTS) return validNames.has(t.projectName);
      if (t.kind === 'epics' && t.projectName !== null) return validNames.has(t.projectName);
      if (t.kind === 'hub') return validNames.has(t.projectName);
      if (t.kind === 'workbenches' && t.projectName !== null) return validNames.has(t.projectName);
      if (t.kind === 'workbench') return validNames.has(t.projectName);
      if (t.kind === 'url-preview') return validNames.has(t.projectName);
      return true;
    });
    if (after.length === before.length) return;
    const activeSurvives = after.some(t => studioTabKey(t) === this._activeKey());
    this._tabs.set(after);
    this.reconcileActivationHistory(after);
    if (!activeSurvives) this.activateMostRecent(after, this.lastKey(after));
    this.persist();
  }

  /** Retarget every project-keyed tab after a registry display-name change. */
  renameProject(previousName: string, currentName: string): void {
    if (!previousName || !currentName || previousName === currentName) return;
    const active = this.activeTab();
    const rename = (tab: StudioTab): StudioTab => {
      switch (tab.kind) {
        case 'board':
          return tab.projectName === previousName ? { ...tab, projectName: currentName } : tab;
        case 'epics':
          return tab.projectName === previousName ? { ...tab, projectName: currentName } : tab;
        case 'hub':
          return tab.projectName === previousName ? { ...tab, projectName: currentName } : tab;
        case 'workbenches':
          return tab.projectName === previousName ? { ...tab, projectName: currentName } : tab;
        case 'workbench':
          return tab.projectName === previousName ? { ...tab, projectName: currentName } : tab;
        case 'url-preview':
          return tab.projectName === previousName ? { ...tab, projectName: currentName } : tab;
        default:
          return tab;
      }
    };
    const before = this._tabs();
    const renamed = before.map(rename);
    const keyMap = new Map(before.map((tab, index) => [studioTabKey(tab), studioTabKey(renamed[index])]));
    const next = this.dedupe(renamed);
    this._tabs.set(next);
    this.activationHistory = this.activationHistory.map(key => keyMap.get(key) ?? key);
    this.reconcileActivationHistory(next);
    if (active) {
      const renamedActiveKey = studioTabKey(rename(active));
      if (next.some((tab) => studioTabKey(tab) === renamedActiveKey)) {
        this.activate(renamedActiveKey);
      } else {
        this.activateMostRecent(next, this.lastKey(next));
      }
    }
    this.persist();
  }

  // ---- persistence ----------------------------------------------------

  /**
   * Hydrate from localStorage. Returns whether a usable snapshot was found
   * so the constructor can decide whether to seed the default board.
   * A valid-but-empty tab list counts as a snapshot: it means the user
   * deliberately closed every tab and should land on the empty-state.
   */
  private restore(): boolean {
    if (typeof window === 'undefined') return false;
    try {
      const raw = window.localStorage?.getItem(STORAGE_KEY);
      if (!raw) return false;
      const parsed = JSON.parse(raw) as PersistedState;
      if (!parsed || parsed.v !== STORAGE_VERSION) return false;
      if (!Array.isArray(parsed.tabs)) return false;
      // Drop retired and future tab kinds that don't round-trip through
      // studioTabKey. This is also the migration for persisted Backlog Triage
      // tabs from builds that still exposed that feature.
      const safeTabs = parsed.tabs.filter(t => {
        try { return typeof studioTabKey(t) === 'string'; }
        catch { return false; }
      });
      this._tabs.set(this.dedupe(safeTabs.map(t => this.normalizeTab(t))));
      // Only restore the active key if it points at a surviving tab.
      const normalizedTabs = this._tabs();
      const validKey = normalizedTabs.some(t => studioTabKey(t) === parsed.activeKey);
      this._activeKey.set(
        validKey
          ? parsed.activeKey
          : (normalizedTabs.length ? studioTabKey(normalizedTabs[normalizedTabs.length - 1]) : null),
      );
      return true;
    } catch {
      // Corrupt payload — drop it. The constructor re-seeds the default.
      return false;
    }
  }

  private persist(): void {
    if (typeof window === 'undefined') return;
    try {
      const payload: PersistedState = {
        v: STORAGE_VERSION,
        tabs: this._tabs(),
        activeKey: this._activeKey(),
      };
      window.localStorage?.setItem(STORAGE_KEY, JSON.stringify(payload));
    } catch {
      /* storage may be full / blocked; signals still reflect live state */
    }
  }

  private normalizeTab(tab: StudioTab): StudioTab {
    switch (tab.kind) {
      case 'board':
        return { kind: 'board', projectName: tab.projectName };
      case 'feed':
        return { kind: 'feed' };
      case 'chat-history':
        return { kind: 'chat-history' };
      case 'epics':
        return { kind: 'epics', projectName: tab.projectName };
      case 'epic':
        return {
          kind: 'epic',
          epicKey: tab.epicKey,
          viewTaskKey: tab.viewTaskKey || undefined,
        };
      case 'task': {
        const scope = normalizeTaskTabScope(tab.scope);
        return { kind: 'task', taskKey: tab.taskKey, ...(scope ? { scope } : {}) };
      }
      case 'hub':
        return {
          kind: 'hub',
          projectName: tab.projectName,
          section: tab.section,
          ...(tab.section === 'wiki' && tab.wikiTarget
            ? { wikiTarget: this.normalizeWikiTarget(tab.wikiTarget) }
            : {}),
          ...(tab.pipelineStepId ? { pipelineStepId: tab.pipelineStepId } : {}),
          ...(tab.documentHistory ? { documentHistory: this.normalizeDocumentHistory(tab.documentHistory) } : {}),
          ...(tab.documentId ? { documentId: tab.documentId } : {}),
        };
      case 'workbenches':
        return { kind: 'workbenches', projectName: tab.projectName };
      case 'workbench':
        return {
          kind: 'workbench',
          projectName: tab.projectName,
          ...(tab.projectId ? { projectId: tab.projectId } : {}),
          workbenchId: tab.workbenchId,
          title: tab.title,
          key: tab.key,
          ...(tab.documentHistory ? { documentHistory: this.normalizeDocumentHistory(tab.documentHistory) } : {}),
          ...(tab.documentId ? { documentId: tab.documentId } : {}),
        };
      case 'diff':
        return { kind: 'diff', commitSha: tab.commitSha };
      case 'activity':
        return { kind: 'activity', taskKey: tab.taskKey };
      case 'url-preview':
        return { kind: 'url-preview', projectName: tab.projectName, urlId: tab.urlId };
      case 'workspace-settings':
        return { kind: 'workspace-settings' };
      case 'welcome':
        return { kind: 'welcome' };
    }
  }

  /**
   * Stamp a new task tab with the scope it is opened in (AGT-2692). An
   * explicit scope from the caller wins; a task that is already open keeps the
   * scope it was created with, because focusing an open tab is not a fresh
   * navigation and must not silently re-scope the app.
   */
  private stampTaskTabScope(tab: StudioTab): StudioTab {
    if (tab.kind !== 'task' || tab.scope) return tab;
    const key = studioTabKey(tab);
    const carried = this.carryTaskTabScope(tab, this._tabs().find(t => studioTabKey(t) === key));
    if (carried !== tab) return carried;
    const scope = resolveTaskTabScope(this.activeTab());
    return scope ? { ...tab, scope } : tab;
  }

  /** Keep the scope a task tab already carries when the incoming payload omits one. */
  private carryTaskTabScope(tab: StudioTab, previous: StudioTab | undefined): StudioTab {
    if (tab.kind !== 'task' || tab.scope) return tab;
    if (previous?.kind !== 'task' || !previous.scope) return tab;
    return { ...tab, scope: previous.scope };
  }

  private replaceActiveDocument(active: Extract<StudioTab, { kind: 'hub' | 'workbench' }>, incoming: Extract<StudioTab, { kind: 'hub' | 'workbench' }>): void {
    const history = active.documentHistory ?? this.initialDocumentHistory(active);
    const target = this.documentTarget(incoming);
    if (this.sameDocumentTarget(history.entries[history.index], target)) return;
    const entries = [...history.entries.slice(0, history.index + 1), target]
      .slice(-DOCUMENT_HISTORY_LIMIT);
    const documentHistory: DocumentTabHistory = { entries, index: entries.length - 1 };
    const next = this.tabFromDocumentTarget(target, active.documentId ?? this.newDocumentId(), documentHistory);
    this.retarget(studioTabKey(active), next);
  }

  private carryDocumentState(tab: StudioTab, previous: StudioTab | undefined): StudioTab {
    if (!this.isDocumentTab(tab) || !this.isDocumentTab(previous)) return tab;
    return {
      ...tab,
      documentId: tab.documentId ?? previous.documentId,
      documentHistory: tab.documentHistory ?? previous.documentHistory,
    };
  }

  private initialDocumentHistory(tab: Extract<StudioTab, { kind: 'hub' | 'workbench' }>): DocumentTabHistory {
    return { entries: [this.documentTarget(tab)], index: 0 };
  }

  private normalizeDocumentHistory(history: DocumentTabHistory): DocumentTabHistory {
    const entries = Array.isArray(history.entries)
      ? history.entries.filter(entry => entry?.kind === 'wiki' || entry?.kind === 'workbench').slice(-DOCUMENT_HISTORY_LIMIT)
      : [];
    if (!entries.length) return { entries: [], index: 0 };
    return { entries, index: Math.max(0, Math.min(Number(history.index) || 0, entries.length - 1)) };
  }

  private documentTarget(tab: Extract<StudioTab, { kind: 'hub' | 'workbench' }>): DocumentTabTarget {
    if (tab.kind === 'hub') {
      return {
        kind: 'wiki',
        projectName: tab.projectName,
        wikiTarget: tab.wikiTarget ?? { kind: 'overview' },
      };
    }
    return {
      kind: 'workbench', projectName: tab.projectName, workbenchId: tab.workbenchId,
      ...(tab.projectId ? { projectId: tab.projectId } : {}),
      ...(tab.title ? { title: tab.title } : {}),
      ...(tab.key ? { key: tab.key } : {}),
    };
  }

  private tabFromDocumentTarget(target: DocumentTabTarget, documentId: string | undefined, documentHistory: DocumentTabHistory): Extract<StudioTab, { kind: 'hub' | 'workbench' }> {
    if (target.kind === 'wiki') {
      return {
        kind: 'hub', projectName: target.projectName, section: 'wiki',
        wikiTarget: target.wikiTarget, documentId, documentHistory,
      };
    }
    return {
      kind: 'workbench', projectName: target.projectName, workbenchId: target.workbenchId,
      ...(target.projectId ? { projectId: target.projectId } : {}),
      ...(target.title ? { title: target.title } : {}),
      ...(target.key ? { key: target.key } : {}),
      documentId, documentHistory,
    };
  }

  private sameDocumentTarget(left: DocumentTabTarget, right: DocumentTabTarget): boolean {
    if (left.kind !== right.kind || left.projectName !== right.projectName) return false;
    if (left.kind === 'workbench' && right.kind === 'workbench') return left.workbenchId === right.workbenchId;
    if (left.kind !== 'wiki' || right.kind !== 'wiki') return false;
    if (left.wikiTarget.kind !== right.wikiTarget.kind) return false;
    if (left.wikiTarget.kind === 'overview' || right.wikiTarget.kind === 'overview') return true;
    return left.wikiTarget.relPath === right.wikiTarget.relPath;
  }

  private isDocumentTab(tab: StudioTab | null | undefined): tab is Extract<StudioTab, { kind: 'hub' | 'workbench' }> {
    return tab?.kind === 'workbench' || (tab?.kind === 'hub' && tab.section === 'wiki');
  }

  private documentHistoryOffset(delta: -1 | 1): DocumentTabTarget | null {
    const tab = this.activeTab();
    if (!this.isDocumentTab(tab)) return null;
    const history = tab.documentHistory ?? this.initialDocumentHistory(tab);
    return history.entries[history.index + delta] ?? null;
  }

  private newDocumentId(): string {
    return globalThis.crypto?.randomUUID?.() ?? `doc-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  }

  /** Collapse duplicate keys, preserving first-seen order. */
  private dedupe(tabs: readonly StudioTab[]): StudioTab[] {
    const next: StudioTab[] = [];
    const seen = new Set<string>();
    for (const tab of tabs) {
      const normalized = this.normalizeTab(tab);
      const key = studioTabKey(normalized);
      if (seen.has(key)) continue;
      seen.add(key);
      next.push(normalized);
    }
    return next;
  }

  private normalizeWikiTarget(target: NonNullable<Extract<StudioTab, { kind: 'hub' }>['wikiTarget']>) {
    if (target.kind === 'overview') return { kind: 'overview' as const };
    return { kind: target.kind, relPath: target.relPath.trim().replace(/^docs\//i, '') };
  }

  private activate(key: string): void {
    this._activeKey.set(key);
    this.recordActivation(key);
  }

  private recordActivation(key: string | null): void {
    if (!key) return;
    this.activationHistory = [...this.activationHistory.filter(item => item !== key), key];
  }

  private reconcileActivationHistory(tabs: readonly StudioTab[]): void {
    const openKeys = new Set(tabs.map(studioTabKey));
    const seen = new Set<string>();
    this.activationHistory = this.activationHistory
      .filter(key => openKeys.has(key))
      .reverse()
      .filter(key => {
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
      })
      .reverse();
  }

  private activateMostRecent(tabs: readonly StudioTab[], fallbackKey: string | null): void {
    this.reconcileActivationHistory(tabs);
    const key = this.activationHistory[this.activationHistory.length - 1] ?? fallbackKey;
    if (key && tabs.some(tab => studioTabKey(tab) === key)) this.activate(key);
    else this._activeKey.set(null);
  }

  private lastKey(tabs: readonly StudioTab[]): string | null {
    return tabs.length ? studioTabKey(tabs[tabs.length - 1]) : null;
  }
}

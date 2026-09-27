import { Injectable, signal } from '@angular/core';

/**
 * Cycle 9 shell feature service: durable user-interface preferences
 * that survive across reloads. Lifted out of `app.ts` per ADR-0034 so
 * the shell stops owning persistence wiring for prefs that have
 * nothing to do with kanban data flow.
 *
 * State backed by localStorage today (the keys match the pre-extraction
 * shell exactly, so the move is invisible to existing users):
 *
 *   - `taskNavCollapsed`  - left task-nav collapsed or expanded
 *   - `compactCards`      - dense vs full job-card mode
 *   - `sideSheetWidth`    - resizable side-sheet width in pixels
 *
 * Methods are deliberately small: no derived state, no computeds, no
 * effects. The service is a thin wrapper around three signals + their
 * persistence so its surface area stays trivial to read.
 *
 * `startResize` lives here because the drag handlers it installs
 * directly call `sideSheetWidth.set` on every mousemove and persist
 * on mouseup; keeping that flow inside the service avoids leaking
 * "I am editing the side-sheet width right now" state into the shell.
 *
 * F24: a `storage` listener mirrors writes from sibling browser tabs
 * back into the local signals so two tabs of the same app converge on
 * a shared UI posture within a single event loop. localStorage is
 * already the persistence channel; the `storage` event is the only
 * way one tab finds out the other one wrote.
 */
const STORAGE_KEY_TASK_NAV = 'taskNavCollapsed';
// AGT-2035: the card-density feature was abolished. `compactCards` is no longer
// a live preference; the key is proactively cleared on boot (see constructor).
const STORAGE_KEY_COMPACT_CARDS = 'compactCards';
const STORAGE_KEY_SIDE_SHEET_WIDTH = 'sideSheetWidth';
const STORAGE_KEY_GROUP_BY_EPIC = 'boardGroupByEpic';
const STORAGE_KEY_OPEN_PROJECT_CHAT_ON_ENTRY = 'atp.studio.openProjectChatOnEntry.v1';
// AGT-1812: the standalone Orchestrator-settings modal was retired into the
// consolidated Settings view (Global → Orchestrator, deep-linkable by URL hash),
// so its bespoke localStorage open-flag is no longer a live preference; the key
// is proactively cleared on boot (see constructor).
const STORAGE_KEY_ORCHESTRATOR_SETTINGS_OPEN = 'orchestratorSettingsOpen';
const STORAGE_KEY_TREE_METRICS = 'atp.studio.explorer.metrics';
const STORAGE_KEY_CHAT_METADATA = 'chat.metadata.enabled';

@Injectable({ providedIn: 'root' })
export class UiPreferencesService {
  readonly taskNavCollapsed = signal<boolean>(localStorage.getItem(STORAGE_KEY_TASK_NAV) === '1');
  readonly treeMetricView = signal<'numbers' | 'dots'>(
    localStorage.getItem(STORAGE_KEY_TREE_METRICS) === 'dots' ? 'dots' : 'numbers',
  );
  readonly sideSheetWidth = signal<number>(
    parseInt(localStorage.getItem(STORAGE_KEY_SIDE_SHEET_WIDTH) ?? '280'),
  );
  /** Project Chat is the standard project entry unless the operator opts out. */
  readonly openProjectChatOnEntry = signal<boolean>(
    localStorage.getItem(STORAGE_KEY_OPEN_PROJECT_CHAT_ON_ENTRY) !== '0',
  );
  /** Null inherits the workspace/project setting; either boolean is this browser user's choice. */
  readonly chatMetadataEnabled = signal<boolean | null>(
    localStorage.getItem(STORAGE_KEY_CHAT_METADATA) === null
      ? null : localStorage.getItem(STORAGE_KEY_CHAT_METADATA) === '1',
  );

  /**
   * Board "Gruppieren nach Epic" toggle. When on, the board case renders the
   * epic tree (`<app-epic-group-board>`) instead of the lane columns. Persisted
   * so the operator's preferred board shape survives reloads.
   */
  readonly groupByEpic = signal<boolean>(localStorage.getItem(STORAGE_KEY_GROUP_BY_EPIC) === '1');

  private resizing = false;

  constructor() {
    // AGT-2035 migration: drop the abolished card-density preference so a stale
    // value can never resurrect compact rendering.
    try { localStorage.removeItem(STORAGE_KEY_COMPACT_CARDS); } catch { /* ignore */ }
    // AGT-1812 migration: the Orchestrator-settings modal was retired into the
    // consolidated Settings view (Global → Orchestrator), which owns its own URL
    // hash. Drop the modal's stale open-flag so a saved '1' can never try to
    // reopen a component that no longer exists.
    try { localStorage.removeItem(STORAGE_KEY_ORCHESTRATOR_SETTINGS_OPEN); } catch { /* ignore */ }
    if (typeof window !== 'undefined') {
      window.addEventListener('storage', this.onStorageEvent);
    }
  }

  /**
   * F24: cross-tab sync. The `storage` event only fires in OTHER tabs
   * when localStorage changes - never in the tab that did the write -
   * so we can mirror unconditionally without re-entry. We tolerate
   * `null` newValue (key cleared) by falling back to the documented
   * default; corrupt int parses fall back to the same default the
   * constructor used.
   */
  private readonly onStorageEvent = (e: StorageEvent): void => {
    if (e.storageArea !== null && e.storageArea !== localStorage) return;
    switch (e.key) {
      case STORAGE_KEY_TASK_NAV:
        this.taskNavCollapsed.set(e.newValue === '1');
        return;
      case STORAGE_KEY_SIDE_SHEET_WIDTH: {
        const parsed = parseInt(e.newValue ?? '280');
        this.sideSheetWidth.set(Number.isFinite(parsed) ? parsed : 280);
        return;
      }
      case STORAGE_KEY_GROUP_BY_EPIC:
        this.groupByEpic.set(e.newValue === '1');
        return;
      case STORAGE_KEY_OPEN_PROJECT_CHAT_ON_ENTRY:
        this.openProjectChatOnEntry.set(e.newValue !== '0');
        return;
      case STORAGE_KEY_TREE_METRICS:
        this.treeMetricView.set(e.newValue === 'dots' ? 'dots' : 'numbers');
        return;
      case STORAGE_KEY_CHAT_METADATA:
        this.chatMetadataEnabled.set(e.newValue === null ? null : e.newValue === '1');
        return;
      default:
        return;
    }
  };

  setTaskNavCollapsed(collapsed: boolean): void {
    this.taskNavCollapsed.set(collapsed);
    localStorage.setItem(STORAGE_KEY_TASK_NAV, collapsed ? '1' : '0');
  }

  setTreeMetricView(value: 'numbers' | 'dots'): void {
    this.treeMetricView.set(value);
    localStorage.setItem(STORAGE_KEY_TREE_METRICS, value);
  }

  setOpenProjectChatOnEntry(open: boolean): void {
    this.openProjectChatOnEntry.set(open);
    localStorage.setItem(STORAGE_KEY_OPEN_PROJECT_CHAT_ON_ENTRY, open ? '1' : '0');
  }

  setChatMetadataEnabled(enabled: boolean): void {
    this.chatMetadataEnabled.set(enabled);
    localStorage.setItem(STORAGE_KEY_CHAT_METADATA, enabled ? '1' : '0');
  }

  toggleGroupByEpic(): void {
    const value = !this.groupByEpic();
    this.groupByEpic.set(value);
    localStorage.setItem(STORAGE_KEY_GROUP_BY_EPIC, value ? '1' : '0');
  }


  /**
   * Side-sheet drag handler. Adds a `body.resizing` class while drag
   * is active so the cursor stays as the resize affordance even when
   * the pointer leaves the handle. Min width 200 px; no max - the
   * sheet can grow as wide as the viewport allows.
   */
  startResize(event: MouseEvent): void {
    event.preventDefault();
    this.resizing = true;
    document.body.classList.add('resizing');

    const startX = event.clientX;
    const startWidth = this.sideSheetWidth();

    const onMouseMove = (e: MouseEvent) => {
      if (!this.resizing) return;
      const deltaX = e.clientX - startX;
      const newWidth = Math.max(200, startWidth + deltaX);
      this.sideSheetWidth.set(newWidth);
    };

    const onMouseUp = () => {
      this.resizing = false;
      document.body.classList.remove('resizing');
      localStorage.setItem(STORAGE_KEY_SIDE_SHEET_WIDTH, this.sideSheetWidth().toString());
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
    };

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }
}

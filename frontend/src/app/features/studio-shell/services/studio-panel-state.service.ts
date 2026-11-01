import { Injectable, signal } from '@angular/core';
import type { StudioPanelKind } from '../studio-shell.types';
import type { StudioActivityMarkerOwner } from '../components/studio-activity-bar/studio-activity-bar.active-key';

const PANEL_STATE_STORAGE_KEY = 'atp.studio.panelState.v1';
const STUDIO_PANEL_KINDS = new Set<StudioPanelKind>([
  'explorer',
  'filters',
  'cli',
  'activity',
  'settings',
  'chats',
]);

interface StudioPanelPersistedState {
  active?: StudioPanelKind;
  visible?: boolean;
  /** Which surface was activated last; drives the ActivityBar marker (AGT-3351). */
  markerOwner?: StudioActivityMarkerOwner;
}

/**
 * Tracks which sidebar panel (Explorer, Tasks, Filters, …) is mounted and
 * whether the sidebar is visible at all. Clicking the same ActivityBar
 * icon a second time collapses the sidebar (VS-Code behaviour), so this
 * service holds both pieces of state and exposes one helper for the
 * toggling rule.
 */
@Injectable({ providedIn: 'root' })
export class StudioPanelStateService {
  private readonly initialState = readPanelState();
  private readonly _active = signal<StudioPanelKind>(this.initialState.active ?? 'explorer');
  private readonly _visible = signal<boolean>(this.initialState.visible ?? true);
  private readonly _markerOwner = signal<StudioActivityMarkerOwner | undefined>(this.initialState.markerOwner);
  // Mockup default width is 240 px (`docs/mockups/vscode-layout/`).
  // The previous 280 px default was a holdover that made the editor
  // pane noticeably narrower than the mockup intended.
  private readonly _sidebarWidth = signal<number>(
    parseInt(localStorage.getItem('atp.studio.sidebarWidth') ?? '240', 10) || 240,
  );
  private readonly _activityBarSide = signal<'left' | 'right'>(
    (localStorage.getItem('atp.studio.activityBarSide') as 'left' | 'right' | null) ?? 'left',
  );

  readonly active = this._active.asReadonly();
  readonly visible = this._visible.asReadonly();
  /** `'panel'` once a sidebar panel was activated after the last editor activation. */
  readonly markerOwner = this._markerOwner.asReadonly();
  readonly sidebarWidth = this._sidebarWidth.asReadonly();
  readonly activityBarSide = this._activityBarSide.asReadonly();

  constructor() {
    // AGT-2035 migration: the standalone "Project chat rail" was removed (its
    // job is covered by the orchestrator multichat). Drop its stored flag so a
    // stale value can never resurrect the rail.
    try { localStorage.removeItem('atp.studio.chatRailOpen'); } catch { /* ignore */ }
  }

  /**
   * VS-Code-style toggle: clicking the active icon hides the sidebar;
   * clicking a different icon switches panel and ensures the sidebar is
   * visible. Returns the resulting visibility for callers that want to
   * react (e.g. status indicator).
   */
  toggle(panel: StudioPanelKind): boolean {
    if (this._active() === panel && this._visible()) {
      this._visible.set(false);
      this.persist();
      return false;
    }
    this._active.set(panel);
    this._visible.set(true);
    this._markerOwner.set('panel');
    this.persist();
    return true;
  }

  open(panel: StudioPanelKind): void {
    this._active.set(panel);
    this._visible.set(true);
    this._markerOwner.set('panel');
    this.persist();
  }

  /**
   * An editor tab was activated (tab strip, rail destination, …). The
   * ActivityBar marker moves back to the main-area destination; the sidebar
   * itself stays as it is.
   */
  noteEditorActivation(): void {
    if (this._markerOwner() === 'editor') return;
    this._markerOwner.set('editor');
    this.persist();
  }

  setVisible(visible: boolean): void {
    this._visible.set(visible);
    this.persist();
  }

  setSidebarWidth(width: number): void {
    const clamped = Math.max(200, Math.min(560, Math.round(width)));
    this._sidebarWidth.set(clamped);
    localStorage.setItem('atp.studio.sidebarWidth', String(clamped));
  }

  setActivityBarSide(side: 'left' | 'right'): void {
    this._activityBarSide.set(side);
    localStorage.setItem('atp.studio.activityBarSide', side);
  }

  private persist(): void {
    writePanelState({
      active: this._active(),
      visible: this._visible(),
      markerOwner: this._markerOwner(),
    });
  }
}

function readPanelState(): StudioPanelPersistedState {
  if (typeof window === 'undefined') return {};
  try {
    const raw = window.localStorage?.getItem(PANEL_STATE_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Partial<StudioPanelPersistedState>;
    let active = typeof parsed.active === 'string' && STUDIO_PANEL_KINDS.has(parsed.active as StudioPanelKind)
      ? parsed.active as StudioPanelKind
      : undefined;
    // The workspace Admin sidebar panel was removed (its destinations live in
    // the CLI/Activity panels). A persisted active:'admin' would land on the
    // "Panel not implemented" fallback, so fall back to the Explorer.
    if ((parsed.active as string) === 'admin') active = 'explorer';
    // AGT-2035 migration: the 'settings' sidebar panel was removed (settings is
    // now a full editor tab). A persisted active:'settings' would land on the
    // "Panel not implemented" fallback, so fall back to the Explorer.
    if (active === 'settings') active = 'explorer';
    // AGT-3351 migration: the Runbook sidebar panel was removed. A persisted
    // active:'runbook' would land on the "Panel not implemented" fallback, so
    // fall back to the Explorer.
    if ((parsed.active as string) === 'runbook') active = 'explorer';
    return {
      active,
      visible: typeof parsed.visible === 'boolean' ? parsed.visible : undefined,
      markerOwner: parsed.markerOwner === 'panel' || parsed.markerOwner === 'editor'
        ? parsed.markerOwner
        : undefined,
    };
  } catch {
    return {};
  }
}

function writePanelState(state: StudioPanelPersistedState): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage?.setItem(PANEL_STATE_STORAGE_KEY, JSON.stringify(state));
  } catch {
    /* storage may be full / blocked */
  }
}

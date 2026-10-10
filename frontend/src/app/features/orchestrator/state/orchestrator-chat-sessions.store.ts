import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import { TaskService } from '../../../services/task.service';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../../../utils/visible-interval';
import type { OrchestratorContextSession } from '../models/orchestrator.model';
import { OrchestratorChatActivityService } from './orchestrator-chat-activity.service';

const SEEN_STORAGE_KEY = 'atp.chatSwitcher.seen.v1';
const POLL_MS = 2_000;

/** The scope the side sheet would chat in right now, offered by "New chat". */
export interface CurrentChatScope {
  readonly contextKey: string;
  readonly kind: 'task' | 'workbench' | 'project';
  readonly label: string;
}

/**
 * One workspace-wide copy of the Task Server session list (AGT-3032). The
 * left-sidebar session manager renders it and the orchestrator side sheet
 * resolves selections and the active-chat indicator against it, so both read
 * the same rows. SignalR `orchestratorContextChanged` triggers a refresh; a
 * bounded visible-tab poll is the convergence fallback for runtime status.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorChatSessionsStore {
  private readonly tasks = inject(TaskService);
  private readonly hub = inject(JobsHubClient);
  private readonly activity = inject(OrchestratorChatActivityService);

  private readonly _sessions = signal<readonly OrchestratorContextSession[]>([]);
  private readonly _loaded = signal(false);
  private readonly _error = signal<string | null>(null);
  private readonly _openContextKey = signal<string | null>(null);
  private readonly _currentScope = signal<CurrentChatScope | null>(null);
  private readonly seen = signal<Record<string, string>>(readSeen());

  readonly sessions = this._sessions.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();
  /** The context shown in the open side sheet; null while the sheet is closed. */
  readonly openContextKey = this._openContextKey.asReadonly();
  readonly currentScope = this._currentScope.asReadonly();

  readonly activeChatCount = computed(() =>
    this.activity.workingContextKeys(this._sessions()).size);

  readonly unreadContextKeys = computed<ReadonlySet<string>>(() => {
    const seen = this.seen();
    const open = this._openContextKey();
    return new Set(this._sessions()
      .filter(session => session.updatedAt && session.contextKey !== open
        && session.updatedAt > (seen[session.contextKey] ?? ''))
      .map(session => session.contextKey));
  });

  private consumers = 0;
  private pollTimer: VisibleIntervalHandle | null = null;
  private inFlight = false;
  /** Archive marks still in flight; a poll answered before the write must not undo them. */
  private readonly archiveOverrides = new Map<string, string | null>();

  constructor() {
    effect(() => {
      this.hub.orchestratorContextsRevision();
      untracked(() => { if (this.consumers > 0) this.refresh(); });
    });
  }

  /** Starts polling for the first consumer; returns the matching release. */
  connect(): () => void {
    this.consumers += 1;
    if (this.consumers === 1) {
      this.refresh();
      this.pollTimer = setVisibleInterval(() => this.refresh(), POLL_MS);
    }
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.consumers -= 1;
      if (this.consumers === 0 && this.pollTimer != null) {
        clearVisibleInterval(this.pollTimer);
        this.pollTimer = null;
      }
    };
  }

  /** A refresh while one is in flight is dropped; the poll converges. */
  refresh(): void {
    if (this.inFlight) return;
    this.inFlight = true;
    this.tasks.getOrchestratorContextSessions().subscribe({
      next: response => {
        this._sessions.set((response.sessions ?? []).map(session =>
          this.archiveOverrides.has(session.contextKey)
            ? { ...session, archivedAt: this.archiveOverrides.get(session.contextKey) ?? null }
            : session));
        this._loaded.set(true);
        this._error.set(null);
        this.inFlight = false;
      },
      error: () => {
        this._loaded.set(true);
        this._error.set('Chats could not be loaded from the Task Server.');
        this.inFlight = false;
      },
    });
  }

  setOpenContext(contextKey: string | null): void {
    this._openContextKey.set(contextKey);
    if (contextKey) this.markSeen(contextKey);
  }

  setCurrentScope(scope: CurrentChatScope | null): void {
    this._currentScope.set(scope);
  }

  markSeen(contextKey: string): void {
    const session = this._sessions().find(item => item.contextKey === contextKey);
    const updatedAt = session?.updatedAt || new Date().toISOString();
    if (this.seen()[contextKey] === updatedAt) return;
    this.seen.update(current => ({ ...current, [contextKey]: updatedAt }));
    writeSeen(this.seen());
  }

  /**
   * Optimistic archive toggle. The row moves at once; a failed request puts
   * it back and surfaces the error, and the next refresh converges either way.
   */
  setArchived(contextKey: string, archived: boolean): void {
    const previous = this._sessions().find(session => session.contextKey === contextKey);
    if (!previous) return;
    const mark = archived ? new Date().toISOString() : null;
    this.archiveOverrides.set(contextKey, mark);
    this.patch(contextKey, { archivedAt: mark });
    this.tasks.setOrchestratorSessionArchived(contextKey, archived).subscribe({
      next: () => {
        this.archiveOverrides.delete(contextKey);
        this._error.set(null);
        this.refresh();
      },
      error: () => {
        this.archiveOverrides.delete(contextKey);
        this.patch(contextKey, { archivedAt: previous.archivedAt ?? null });
        this._error.set(archived ? 'The chat could not be archived.' : 'The chat could not be restored.');
      },
    });
  }

  private patch(contextKey: string, changes: Partial<OrchestratorContextSession>): void {
    this._sessions.update(sessions => sessions.map(session =>
      session.contextKey === contextKey ? { ...session, ...changes } : session));
  }
}

function readSeen(): Record<string, string> {
  if (typeof window === 'undefined') return {};
  try { return JSON.parse(window.localStorage?.getItem(SEEN_STORAGE_KEY) ?? '{}') ?? {}; }
  catch { return {}; }
}

function writeSeen(seen: Record<string, string>): void {
  if (typeof window === 'undefined') return;
  try { window.localStorage?.setItem(SEEN_STORAGE_KEY, JSON.stringify(seen)); }
  catch { /* optional local read state */ }
}

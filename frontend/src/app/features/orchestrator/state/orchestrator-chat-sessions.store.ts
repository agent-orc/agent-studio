import { Injectable, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { Subject } from 'rxjs';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import { TaskService } from '../../../services/task.service';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { OrchestratorContextSession } from '../models/orchestrator.model';

const SEEN_STORAGE_KEY = 'atp.chatSwitcher.seen.v1';
const POLL_MS = 2_000;

/** Requests from the Chats sidebar panel to the orchestrator side sheet. */
export type ChatSessionRequest =
  | { readonly kind: 'open'; readonly contextKey: string }
  | { readonly kind: 'navigate'; readonly contextKey: string };

/**
 * One app-wide projection of the Task Server context list. The Chats sidebar
 * panel and the orchestrator side sheet read the same sessions, the same
 * unread state, and the same open-session key, so the panel can highlight the
 * session the sheet shows and the sheet can open what the panel selects.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorChatSessionsStore implements OnDestroy {
  private readonly tasks = inject(TaskService);
  private readonly hub = inject(JobsHubClient);

  /** Writable so the side sheet can seed a fresher response it already holds. */
  readonly sessions = signal<OrchestratorContextSession[]>([]);
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  /** Context key shown in the open side sheet; null while the sheet is closed. */
  readonly openContextKey = signal<string | null>(null);
  /** Scope the side sheet follows from navigation; offered first under "New chat". */
  readonly navigationContextKey = signal<string | null>(null);
  readonly archiveError = signal<string | null>(null);

  private readonly seen = signal<Record<string, string>>(readSeen());
  private readonly requestSubject = new Subject<ChatSessionRequest>();
  readonly requests = this.requestSubject.asObservable();

  readonly unreadContextKeys = computed<ReadonlySet<string>>(() => {
    const seen = this.seen();
    const open = this.openContextKey();
    return new Set(this.sessions()
      .filter(session => session.updatedAt && session.contextKey !== open
        && session.updatedAt > (seen[session.contextKey] ?? ''))
      .map(session => session.contextKey));
  });

  private attachments = 0;
  private pollTimer: VisibleIntervalHandle | null = null;
  private loading = false;

  constructor() {
    let lastRevision: number | null = null;
    effect(() => {
      const revision = this.hub.orchestratorContextsRevision();
      const changed = lastRevision !== null && revision !== lastRevision;
      lastRevision = revision;
      untracked(() => {
        if (changed && this.attachments > 0) this.refresh();
      });
    });
  }

  /**
   * Starts the bounded convergence poll while at least one surface shows
   * sessions. SignalR is the fast path; the poll keeps live status honest
   * when the hub is reconnecting. Returns the matching detach.
   */
  attach(): () => void {
    this.attachments++;
    if (this.pollTimer === null) {
      this.pollTimer = setVisibleInterval(() => this.refresh(), POLL_MS);
    }
    this.refresh();
    let attached = true;
    return () => {
      if (!attached) return;
      attached = false;
      this.attachments = Math.max(0, this.attachments - 1);
      if (this.attachments === 0) this.stopPolling();
    };
  }

  /** One request at a time; a tick that lands mid-flight is dropped, the poll converges. */
  refresh(): void {
    if (this.loading) return;
    this.loading = true;
    this.tasks.getOrchestratorContextSessions().subscribe({
      next: response => {
        const sessions = response.sessions ?? [];
        this.baselineSeen(sessions);
        this.sessions.set(sessions);
        this.loaded.set(true);
        this.error.set(null);
        this.loading = false;
      },
      error: () => {
        this.error.set('Chats could not be loaded from the Task Server.');
        this.loaded.set(true);
        this.loading = false;
      },
    });
  }

  /**
   * Archives or restores one session. The row moves immediately; the server
   * response (or the next refresh) is authoritative, and a failure restores
   * the previous value.
   */
  setArchived(contextKey: string, archived: boolean): void {
    const previous = this.sessions().find(session => session.contextKey === contextKey);
    if (!previous) return;
    this.archiveError.set(null);
    this.patch(contextKey, {
      archivedAt: archived ? new Date().toISOString() : null,
      // Restoring a session also lifts the automatic task-archive hide in the
      // list until the next refresh says otherwise.
      ...(archived ? {} : { hiddenAt: null }),
    });
    this.tasks.setOrchestratorContextArchived(contextKey, archived).subscribe({
      next: () => this.refresh(),
      error: () => {
        this.patch(contextKey, { archivedAt: previous.archivedAt ?? null, hiddenAt: previous.hiddenAt ?? null });
        this.archiveError.set(archived ? 'The chat could not be archived.' : 'The chat could not be restored.');
      },
    });
  }

  markSeen(contextKey: string): void {
    const session = this.sessions().find(item => item.contextKey === contextKey);
    const updatedAt = session?.updatedAt || new Date().toISOString();
    this.seen.update(current => ({ ...current, [contextKey]: updatedAt }));
    writeSeen(this.seen());
  }

  requestOpen(contextKey: string): void {
    this.requestSubject.next({ kind: 'open', contextKey });
  }

  requestNavigate(contextKey: string): void {
    this.requestSubject.next({ kind: 'navigate', contextKey });
  }

  ngOnDestroy(): void {
    this.stopPolling();
    this.requestSubject.complete();
  }

  /**
   * A session this browser has never observed starts as read: unread marks
   * activity since the operator last saw the list, not the whole history.
   */
  private baselineSeen(sessions: readonly OrchestratorContextSession[]): void {
    const seen = this.seen();
    const missing = sessions.filter(session => session.updatedAt && !(session.contextKey in seen));
    if (missing.length === 0) return;
    const next = { ...seen };
    for (const session of missing) next[session.contextKey] = session.updatedAt;
    this.seen.set(next);
    writeSeen(next);
  }

  private patch(contextKey: string, changes: Partial<OrchestratorContextSession>): void {
    this.sessions.update(sessions => sessions.map(session =>
      session.contextKey === contextKey ? { ...session, ...changes } : session));
  }

  private stopPolling(): void {
    clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }
}

function readSeen(): Record<string, string> {
  if (typeof window === 'undefined') return {};
  try { return JSON.parse(window.localStorage?.getItem(SEEN_STORAGE_KEY) ?? '{}'); }
  catch { return {}; }
}

function writeSeen(seen: Record<string, string>): void {
  if (typeof window === 'undefined') return;
  try { window.localStorage?.setItem(SEEN_STORAGE_KEY, JSON.stringify(seen)); }
  catch { /* optional local read state */ }
}

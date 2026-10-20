import { Injectable, computed, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';
import type { OperationalStallCase, OrchestratorLogEntry } from '../models/orchestrator.model';

const ALERTS_SEEN_AT_KEY = 'atp.orchestrator-feed.alerts-seen-at';
const POLL_INTERVAL_MS = 10_000;

/**
 * One workspace-wide feed snapshot shared by the main Feed route, its alert
 * badge, and the optional quick-access overlay. Stable row identities keep a
 * quiet poll from repainting the visible history when nothing changed.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorFeedStore {
  private readonly tasks = inject(TaskService);
  private readonly _entries = signal<OrchestratorLogEntry[]>([]);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);
  private readonly _openStalls = signal<OperationalStallCase[]>([]);
  private readonly _alertsSeenAt = signal(this.readAlertsSeenAt());
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private loaded = false;

  readonly entries = this._entries.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();
  readonly openStalls = this._openStalls.asReadonly();
  readonly openStallCount = computed(() => this._openStalls().length);
  readonly freshAlertCount = computed(() => {
    const seenAt = this._alertsSeenAt();
    return this._entries().filter(entry => entry.kind === 'alert' && entry.ts > seenAt).length;
  });

  start(): void {
    if (this.pollTimer === null) {
      this.pollTimer = setInterval(() => this.refresh(true), POLL_INTERVAL_MS);
    }
    if (!this.loaded && !this._loading()) this.refresh();
  }

  stop(): void {
    if (this.pollTimer === null) return;
    clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(silent = false): void {
    if (this._loading()) return;
    this.tasks.getOperationalStalls().subscribe({
      next: response => this._openStalls.set(Array.isArray(response?.cases) ? response.cases : []),
      error: () => this._openStalls.set([]),
    });
    if (!silent) this._loading.set(true);
    this.tasks.getGlobalOrchestratorFeed().subscribe({
      next: response => {
        // Shape guard: a proxy/mock or older backend may answer with a bare
        // array or an envelope without entries. A non-array must never reach
        // reuseStableEntries (`next.map` crashed app-wide via the global error
        // dialog when the feed met a `[]` catch-all mock, sweep 31.07.).
        const entries = Array.isArray(response?.entries)
          ? response.entries
          : Array.isArray(response) ? (response as unknown as OrchestratorLogEntry[]) : [];
        const next = this.reuseStableEntries(entries);
        if (!this.sameReferences(this._entries(), next)) this._entries.set(next);
        this.initialiseAlertBaseline(next);
        this.loaded = true;
        this._error.set(null);
        this._loading.set(false);
      },
      error: error => {
        this._error.set(error?.error?.error || error?.message || 'Failed to load orchestrator feed');
        this._loading.set(false);
      },
    });
  }

  markAlertsSeen(): void {
    const latest = this.latestAlertTimestamp(this._entries());
    if (!latest || latest <= this._alertsSeenAt()) return;
    this._alertsSeenAt.set(latest);
    this.writeAlertsSeenAt(latest);
  }

  reportError(message: string): void {
    this._error.set(message);
  }

  private initialiseAlertBaseline(entries: readonly OrchestratorLogEntry[]): void {
    if (this._alertsSeenAt()) return;
    const latest = this.latestAlertTimestamp(entries);
    if (!latest) return;
    this._alertsSeenAt.set(latest);
    this.writeAlertsSeenAt(latest);
  }

  private latestAlertTimestamp(entries: readonly OrchestratorLogEntry[]): string {
    return entries
      .filter(entry => entry.kind === 'alert')
      .reduce((latest, entry) => entry.ts > latest ? entry.ts : latest, '');
  }

  private reuseStableEntries(next: readonly OrchestratorLogEntry[]): OrchestratorLogEntry[] {
    const previous = new Map(this._entries().map(entry => [this.entryKey(entry), entry]));
    return next.map(entry => {
      const existing = previous.get(this.entryKey(entry));
      return existing && JSON.stringify(existing) === JSON.stringify(entry) ? existing : entry;
    });
  }

  private sameReferences(
    current: readonly OrchestratorLogEntry[],
    next: readonly OrchestratorLogEntry[],
  ): boolean {
    return current.length === next.length && current.every((entry, index) => entry === next[index]);
  }

  private entryKey(entry: OrchestratorLogEntry): string {
    return [entry.ts, entry.project, entry.kind, entry.topic, entry.jobId, entry.summary].join('\u0000');
  }

  private readAlertsSeenAt(): string {
    if (typeof window === 'undefined') return '';
    try {
      return window.localStorage?.getItem(ALERTS_SEEN_AT_KEY) ?? '';
    } catch {
      return '';
    }
  }

  private writeAlertsSeenAt(value: string): void {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage?.setItem(ALERTS_SEEN_AT_KEY, value);
    } catch {
      // The live signal remains authoritative when storage is unavailable.
    }
  }
}

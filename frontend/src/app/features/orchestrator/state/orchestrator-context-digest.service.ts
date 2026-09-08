import { Injectable, computed, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';
import { formatRelativeTime } from '../../../services/format.util';
import { NowTickService } from '../../../services/now-tick.service';
import type { OrchestratorContextDigest } from '../models/orchestrator.model';
import { orchestratorContextErrorMessage } from '../components/orchestrator-side-sheet/orchestrator-context-key.util';

/** Per-sheet state for the context-keyed ORCH-1 application digest. */
@Injectable()
export class OrchestratorContextDigestService {
  private readonly taskService = inject(TaskService);
  private readonly nowTick = inject(NowTickService);
  private readonly contextKey = signal<string | null>(null);
  private requestVersion = 0;

  readonly digest = signal<OrchestratorContextDigest | null>(null);
  readonly loading = signal(false);
  readonly refreshing = signal(false);
  readonly error = signal<string | null>(null);

  readonly activeDigest = computed<OrchestratorContextDigest | null>(() => {
    const digest = this.digest();
    return digest?.contextKey === this.contextKey() ? digest : null;
  });

  readonly scopeLabel = computed<string>(() => {
    const key = this.contextKey();
    if (key === 'global') return 'Global context';
    if (key?.startsWith('task:')) return 'Task context';
    if (key?.startsWith('workbench:')) return 'Dossier context';
    if (key?.startsWith('project:')) return 'Project context';
    return 'No context';
  });

  readonly statusText = computed<string>(() => {
    const digest = this.activeDigest();
    const freshness = digest ? formatRelativeTime(digest.capturedAt, this.nowTick.now()) : '';
    if (this.refreshing()) {
      return freshness ? `Refreshing context · captured ${freshness}` : 'Refreshing context…';
    }
    if (this.error()) {
      return freshness ? `Refresh failed · captured ${freshness}` : 'Context unavailable';
    }
    if (this.loading() && !digest) return 'Loading context…';
    return freshness ? `Context captured ${freshness}` : 'Context not loaded';
  });

  readonly statusTitle = computed<string>(() => {
    const digest = this.activeDigest();
    const parts = digest ? [`Captured ${digest.capturedAt}`] : [];
    if (this.error()) parts.push(this.error()!);
    return parts.join(' · ') || 'No context digest has been captured yet';
  });

  selectContext(contextKey: string | null): void {
    if (contextKey === this.contextKey()) return;
    this.requestVersion += 1;
    this.contextKey.set(contextKey);
    this.error.set(null);
    this.loading.set(false);
    this.refreshing.set(false);
  }

  /** GET is the cheap live read; POST /refresh explicitly re-probes quota. */
  load(contextKey: string, force: boolean): void {
    this.selectContext(contextKey);
    const requestVersion = ++this.requestVersion;
    this.error.set(null);
    if (force) this.refreshing.set(true);
    else this.loading.set(true);
    const request = force
      ? this.taskService.refreshOrchestratorContextDigest(contextKey)
      : this.taskService.getOrchestratorContextDigest(contextKey);
    request.subscribe({
      next: digest => {
        if (this.contextKey() !== contextKey || this.requestVersion !== requestVersion) return;
        this.digest.set(digest);
        this.error.set(null);
        this.loading.set(false);
        this.refreshing.set(false);
      },
      error: err => {
        if (this.contextKey() !== contextKey || this.requestVersion !== requestVersion) return;
        this.error.set(orchestratorContextErrorMessage(err, 'Failed to refresh orchestrator context'));
        this.loading.set(false);
        this.refreshing.set(false);
      },
    });
  }
}

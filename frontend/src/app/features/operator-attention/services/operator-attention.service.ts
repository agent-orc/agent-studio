import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { groupAttention, type OperatorAttentionItem, type OperatorAttentionResponse } from '../models/operator-attention.model';
import { CredentialInventoryService } from '../../credentials/credential-inventory.service';
import { isBackupOverview, type BackupOverview } from '../../backups/backup-status.model';
import { ProjectDocsService } from '../../../services/project-docs.service';

const POLL_INTERVAL_MS = 15_000;

/**
 * The attention list (AGT-3576): the open facts of the operator decision
 * stream, read as one current list. The header count is the length of the
 * same array the list renders, so the two cannot disagree (R3).
 */
@Injectable({ providedIn: 'root' })
export class OperatorAttentionService {
  private readonly http = inject(HttpClient);
  private readonly inventory = inject(CredentialInventoryService);
  private readonly docs = inject(ProjectDocsService);
  private readonly _facts = signal<readonly OperatorAttentionItem[]>([]);
  private readonly _dossiers = signal<readonly OperatorAttentionItem[]>([]);
  private readonly _backup = signal<OperatorAttentionItem | null>(null);
  private readonly _loaded = signal(false);
  private readonly _error = signal(false);
  private timer: ReturnType<typeof setInterval> | null = null;
  private inFlight = false;
  private lastDossierRead = 0;

  readonly items = computed(() => [
    ...this._facts(),
    ...this._dossiers(),
    ...(this._backup() ? [this._backup()!] : []),
    ...((this.inventory.inventory()?.items ?? [])
      .filter(item => ['expiring', 'missing', 'unexpected'].includes(item.state))
      .map(item => ({
        id: `installation.credential|${item.id}`, type: 'installation.credential', project: null,
        cardKey: null, cards: [], title: item.label,
        reason: item.detail || `Credential ${item.state}.`,
        observedAt: this.inventory.inventory()!.generatedAt, suggestedOperations: [],
      }))),
  ]);
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();
  readonly count = computed(() => this.items().length);
  readonly groups = computed(() => groupAttention(this.items()));

  start(): void {
    if (this.timer !== null) return;
    this.inventory.ensureLoaded();
    this.refresh();
    this.timer = setInterval(() => {
      if (!document.hidden) this.refresh();
    }, POLL_INTERVAL_MS);
  }

  stop(): void {
    if (this.timer === null) return;
    clearInterval(this.timer);
    this.timer = null;
  }

  refresh(): void {
    if (Date.now() - this.lastDossierRead >= 30_000) {
      this.lastDossierRead = Date.now();
      this.docs.getWorkbenchOverview().subscribe({
        next: overview => this._dossiers.set((overview.items ?? [])
          .filter(({ workbench }) => workbench.status === 'decision-pending'
            && (workbench.openDecisionCount ?? 1) > 0)
          .map(({ projectName, workbench }) => {
            const count = workbench.openDecisionCount ?? 1;
            return {
              id: `dossier|${projectName}|${workbench.id}`, type: 'dossier.decision-open',
              project: projectName, cardKey: workbench.key ?? null, cards: [],
              title: workbench.title, dossierId: workbench.id,
              reason: `${count} open ${count === 1 ? 'decision' : 'decisions'}`,
              observedAt: workbench.updatedAtUtc, suggestedOperations: [],
            };
          })),
        error: () => { this.lastDossierRead = 0; },
      });
    }
    this.http.get<BackupOverview>('/api/operator/backups').subscribe({
      next: value => {
        if (!isBackupOverview(value)) return;
        const needsAttention = value.state === 'disabled' || value.state === 'not-configured' || value.stale;
        this._backup.set(needsAttention ? {
          id: 'installation.backups', type: 'installation.backups', project: null, cardKey: null,
          cards: [], title: 'Backups', reason: value.stale ? 'Backups stale.' : 'Backups off.',
          observedAt: value.generatedAt, suggestedOperations: [],
        } : null);
      },
      error: () => this._backup.set(null),
    });
    if (this.inFlight) return;
    this.inFlight = true;
    this.http.get<OperatorAttentionResponse>('/api/operator/attention').subscribe({
      next: response => {
        this.inFlight = false;
        const items = Array.isArray(response?.items) ? response.items : [];
        if (!sameRows(this._facts(), items)) this._facts.set(items);
        this._loaded.set(true);
        this._error.set(false);
      },
      error: () => {
        this.inFlight = false;
        this._error.set(true);
      },
    });
  }
}

function sameRows(current: readonly OperatorAttentionItem[], next: readonly OperatorAttentionItem[]): boolean {
  return current.length === next.length
    && current.every((item, index) => item.id === next[index].id && item.reason === next[index].reason
      && item.title === next[index].title);
}

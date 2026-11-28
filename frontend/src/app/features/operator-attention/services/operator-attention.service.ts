import { HttpClient } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { groupAttention, isAttentionDecision, type OperatorAttentionItem, type OperatorAttentionResponse } from '../models/operator-attention.model';
import type { AcceptanceReadinessItem, AcceptanceReadinessReport } from '../../acceptance-readiness';
import { CredentialInventoryService, type CredentialInventory } from '../../credentials/credential-inventory.service';
import { isBackupOverview, type BackupOverview } from '../../backups/backup-status.model';
import { ProjectDocsService } from '../../../services/project-docs.service';

const POLL_INTERVAL_MS = 15_000;
type ReadSource = 'attention' | 'backups' | 'dossiers' | 'credentials';
type CoverageState = 'pending' | 'current' | 'unavailable' | 'stale';

/**
 * The attention list (AGT-3576): the open facts of the operator decision
 * stream, read as one current list. The header counts each section separately.
 */
@Injectable({ providedIn: 'root' })
export class OperatorAttentionService {
  private readonly http = inject(HttpClient);
  private readonly inventory = inject(CredentialInventoryService);
  private readonly docs = inject(ProjectDocsService);
  private readonly _facts = signal<readonly OperatorAttentionItem[]>([]);
  private readonly _dossiers = signal<readonly OperatorAttentionItem[]>([]);
  private readonly _archivedProjects = signal<ReadonlySet<string>>(new Set());
  private readonly _backup = signal<OperatorAttentionItem | null>(null);
  private readonly _coverage = signal<Record<ReadSource, CoverageState>>({
    attention: 'pending', backups: 'pending', dossiers: 'pending', credentials: 'pending',
  });
  private readonly _credentials = signal<CredentialInventory | null>(null);
  private readonly _loaded = signal(false);
  private readonly _error = signal(false);
  private readonly _readiness = signal<ReadonlyMap<string, AcceptanceReadinessItem>>(new Map());
  private readonly _readinessChecked = signal(false);
  private readonly _readinessLoading = signal(false);
  private readonly _readinessError = signal(false);
  private readinessActive = false;
  private readinessInFlight = false;
  private readinessQueued = false;
  private readinessVersion = 0;
  private timer: ReturnType<typeof setInterval> | null = null;
  private inFlight = false;
  private backupInFlight = false;
  private dossierInFlight = false;
  private lastDossierRead = 0;

  readonly items = computed<readonly OperatorAttentionItem[]>(() => [
    ...this._facts().filter(item => !item.project || !this._archivedProjects().has(item.project.toLowerCase())),
    ...this._dossiers().filter(item => !item.project || !this._archivedProjects().has(item.project.toLowerCase())),
    ...(this._backup() ? [this._backup()!] : []),
    ...((this._credentials()?.items ?? [])
      .filter(item => ['expiring', 'missing', 'unexpected'].includes(item.state))
      .map(item => ({
        id: `installation.credential|${item.id}`, type: 'installation.credential', project: null,
        cardKey: null, cards: [], title: item.label,
        reason: item.detail || `Credential ${item.state}.`,
        observedAt: this._credentials()!.generatedAt, suggestedOperations: [],
      }))),
  ]);
  readonly coverage = computed(() => [
    { source: 'Attention', state: this._coverage().attention },
    { source: 'Backups', state: this._coverage().backups },
    { source: 'Dossiers', state: this._coverage().dossiers },
    { source: 'Credentials', state: this._coverage().credentials },
  ]);
  readonly complete = computed(() => this.coverage().every(source => source.state === 'current'));
  readonly coverageLabel = computed(() => this.complete() ? ''
    : this.coverage().some(source => source.state === 'stale' || source.state === 'unavailable')
      ? 'Partial coverage' : 'Checking coverage');
  readonly coverageDetail = computed(() => this.coverage().filter(source => source.state !== 'current')
    .map(source => `${source.source}: ${source.state}`).join('; '));

  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();
  readonly decisions = computed(() => this.items().filter(isAttentionDecision));
  readonly count = computed(() => this.decisions().length);
  readonly noticeCount = computed(() => this.items().length - this.count());
  readonly sections = computed(() => [
    { label: 'Decisions', groups: groupAttention(this.decisions()) },
    { label: 'Notices', groups: groupAttention(this.items().filter(item => !isAttentionDecision(item))) },
  ]);
  readonly reviewCount = computed(() => this._facts().filter(item => item.type === 'human-review.ready'
    && (!item.project || !this._archivedProjects().has(item.project.toLowerCase()))).length);
  readonly readyCount = computed(() => this._facts().filter(item => item.type === 'human-review.ready'
    && (!item.project || !this._archivedProjects().has(item.project.toLowerCase()))
    && this.readinessFor(item)?.readiness === 'ready').length);
  readonly readinessChecked = this._readinessChecked.asReadonly();
  readonly readinessLoading = this._readinessLoading.asReadonly();
  readonly readinessError = this._readinessError.asReadonly();

  constructor() {
    // Inventory clears its own value on error. Keep the last successful facts in this projection.
    effect(() => {
      const inventory = this.inventory.inventory();
      const state = this.inventory.state();
      if (state === 'unsupported') {
        this._credentials.set(null);
        this.recordRead('credentials', true);
      } else if (state === 'ready' && inventory) {
        this._credentials.set(inventory);
        this.recordRead('credentials', true);
      } else if (state === 'unavailable') this.recordRead('credentials', false);
    });
  }

  async dismiss(items: readonly OperatorAttentionItem[], reason: string): Promise<void> {
    const ids = items.map(item => item.id);
    await firstValueFrom(this.http.post('/api/operator/attention/dismiss', { ids, reason }));
    this._facts.update(facts => facts.filter(item => !ids.includes(item.id)));
    this.refresh();
  }

  async runAnyway(item: OperatorAttentionItem): Promise<void> {
    if (!item.project) return;
    await firstValueFrom(this.http.post(`/api/projects/${encodeURIComponent(item.project)}/dispatch/run-anyway`, { fact: item.id }));
    this._facts.update(items => items.filter(fact => fact.id !== item.id));
    this.refresh();
  }

  setArchivedProjects(names: readonly string[]): void {
    this._archivedProjects.set(new Set(names.map(name => name.toLowerCase())));
  }

  readinessFor(item: OperatorAttentionItem): AcceptanceReadinessItem | null {
    return item.project && item.cardKey
      ? this._readiness().get(readinessKey(item.project, item.cardKey)) ?? null : null;
  }

  /** Completion previews run only while the owner has the list open. */
  setReadinessActive(active: boolean): void {
    if (this.readinessActive === active) return;
    this.readinessActive = active;
    this.readinessVersion++;
    if (active) {
      this._readiness.set(new Map());
      this._readinessChecked.set(false);
      this._readinessError.set(false);
    } else {
      this.readinessQueued = false;
      this._readinessLoading.set(false);
    }
  }

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
    this.setReadinessActive(false);
  }

  refresh(): void {
    if (!this.dossierInFlight && Date.now() - this.lastDossierRead >= 30_000) {
      this.dossierInFlight = true;
      this.lastDossierRead = Date.now();
      this.docs.getWorkbenchOverview().subscribe({
        next: overview => {
          this.dossierInFlight = false;
          if (!Array.isArray(overview?.items)) {
            this.recordRead('dossiers', false);
            this.lastDossierRead = 0;
            return;
          }
          this.recordRead('dossiers', true);
          this._dossiers.set(overview.items
            .filter(({ workbench }) => workbench.status === 'decision-pending'
              && (workbench.openDecisionCount ?? 1) > 0)
            .map(({ projectName, workbench }) => {
              const count = workbench.openDecisionCount;
              return {
                id: `dossier|${projectName}|${workbench.id}`, type: 'dossier.decision-open',
                project: projectName, cardKey: workbench.key ?? null, cards: [],
                title: workbench.title, dossierId: workbench.id,
                reason: count == null ? 'Decision count not observed. Open the Dossier to inspect its questions.'
                  : `${count} open ${count === 1 ? 'decision' : 'decisions'}`,
                observedAt: workbench.updatedAtUtc, suggestedOperations: [],
              };
            }));
        },
        error: () => {
          this.dossierInFlight = false;
          this.recordRead('dossiers', false);
          this.lastDossierRead = 0;
        },
      });
    }
    if (!this.backupInFlight) {
      this.backupInFlight = true;
      this.http.get<BackupOverview>('/api/operator/backups').subscribe({
        next: value => {
          this.backupInFlight = false;
          if (!isBackupOverview(value)) {
            this.recordRead('backups', false);
            return;
          }
          this.recordRead('backups', true);
          const needsAttention = value.state === 'disabled' || value.state === 'not-configured' || value.stale;
          this._backup.set(needsAttention ? {
            id: 'installation.backups', type: 'installation.backups', project: null, cardKey: null,
            cards: [], title: 'Backups', reason: value.stale ? 'Backups stale.' : 'Backups off.',
            observedAt: value.generatedAt, suggestedOperations: [],
          } : null);
        },
        error: () => {
          this.backupInFlight = false;
          this.recordRead('backups', false);
        },
      });
    }
    if (this.inFlight) return;
    this.inFlight = true;
    this.http.get<OperatorAttentionResponse>('/api/operator/attention').subscribe({
      next: response => {
        this.inFlight = false;
        if (!Array.isArray(response?.items)) {
          this.recordRead('attention', false);
          this._error.set(true);
          return;
        }
        const items = response.items;
        this.recordRead('attention', true);
        if (!sameRows(this._facts(), items)) this._facts.set(items);
        if (this.readinessActive) void this.refreshReadiness();
        this._loaded.set(true);
        this._error.set(false);
      },
      error: () => {
        this.inFlight = false;
        this._error.set(true);
        this.recordRead('attention', false);
      },
    });
  }

  private recordRead(source: ReadSource, success: boolean): void {
    this._coverage.update(coverage => ({ ...coverage, [source]: success ? 'current'
      : coverage[source] === 'current' || coverage[source] === 'stale' ? 'stale' : 'unavailable' }));
  }

  private async refreshReadiness(): Promise<void> {
    if (this.readinessInFlight) {
      this.readinessQueued = true;
      return;
    }
    this.readinessInFlight = true;
    const cards = this._facts().filter(item => item.type === 'human-review.ready'
      && (!item.project || !this._archivedProjects().has(item.project.toLowerCase()))
      && item.project && item.cardKey);
    const version = ++this.readinessVersion;
    this._readinessLoading.set(cards.length > 0);
    this._readinessError.set(false);
    if (cards.length === 0) {
      this._readiness.set(new Map());
      this._readinessChecked.set(true);
      this.readinessInFlight = false;
      return;
    }
    try {
      const found = new Map<string, AcceptanceReadinessItem>();
      // The endpoint caps named cards at 500 per request.
      for (let offset = 0; offset < cards.length; offset += 500) {
        const batch = cards.slice(offset, offset + 500);
        const report = await firstValueFrom(this.http.post<AcceptanceReadinessReport>(
          '/api/tasks/acceptance-readiness',
          { items: batch.map(item => ({ jobId: item.cardKey, project: item.project })) },
        ));
        for (const item of report.items) {
          found.set(readinessKey(item.projectName, item.taskKey || item.jobId), item);
          if (item.key) found.set(readinessKey(item.projectName, item.key), item);
        }
      }
      if (version !== this.readinessVersion || !this.readinessActive) return;
      this._readiness.set(found);
      this._readinessChecked.set(true);
    } catch {
      if (version !== this.readinessVersion || !this.readinessActive) return;
      this._readiness.set(new Map());
      this._readinessError.set(true);
      this._readinessChecked.set(false);
    } finally {
      if (version === this.readinessVersion) this._readinessLoading.set(false);
      this.readinessInFlight = false;
      if (this.readinessQueued && this.readinessActive) {
        this.readinessQueued = false;
        void this.refreshReadiness();
      }
    }
  }
}

function readinessKey(project: string, card: string): string {
  return `${project}\u0000${card}`;
}

function sameRows(current: readonly OperatorAttentionItem[], next: readonly OperatorAttentionItem[]): boolean {
  return current.length === next.length
    && current.every((item, index) => item.id === next[index].id && item.reason === next[index].reason
      && item.title === next[index].title && item.observedAt === next[index].observedAt
      && item.isDecision === next[index].isDecision && item.canDismiss === next[index].canDismiss
      && JSON.stringify(item.ownerOptions ?? []) === JSON.stringify(next[index].ownerOptions ?? []));
}

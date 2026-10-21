import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { AuthSessionState } from '../../../../services/auth.service';
import { formatUsdExact } from '../../../usage-cockpit/usage-chip.util';
import type { WorkspaceQuotaStatus } from '../../state/workspace-quota-board-state.service';

interface QuotaLimits {
  modelTokensPerWeek: number | null;
  computeCoreHoursPerWeek: number | null;
  hostingSlots: number | null;
}
interface QuotaUsage { modelTokens: number; modelCostUsd: number; computeCoreHours: number; hostingSlots: number }
interface QuotaConsumer { projectId: string; project: string; cardId: string | null; modelTokens: number; computeCoreHours: number }
interface QuotaProjectConsumer { projectId: string; project: string; modelTokens: number; computeCoreHours: number }
interface QuotaSnapshot {
  workspaceId: string; projectId: string | null; weekStartUtc: string; weekEndUtc: string;
  limits: QuotaLimits | null; usage: QuotaUsage; remaining: QuotaUsage;
  status: WorkspaceQuotaStatus; biggestConsumers: QuotaConsumer[]; biggestProjects: QuotaProjectConsumer[]; dailyTrend: QuotaUsage[];
  meterComplete: boolean; overrideActive: boolean; codingAllowed: boolean; hostingAllowed: boolean; message: string | null;
}

const DAY_MS = 86_400_000;
const NO_LIMITS: QuotaLimits = { modelTokensPerWeek: null, computeCoreHoursPerWeek: null, hostingSlots: null };
const STATE_LABELS: Record<WorkspaceQuotaStatus, string> = {
  ok: 'Within quota', warning: 'Warning', blocked: 'Blocked', 'hosting-full': 'Hosting slots full',
};

@Component({
  selector: 'app-workspace-quota',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-quota.component.html',
  styleUrl: './workspace-quota.component.scss',
})
export class WorkspaceQuotaComponent {
  readonly workspaceId = input.required<string>();
  readonly projectId = input<string | null>(null);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  readonly canEdit = () => this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner';
  readonly snapshot = signal<QuotaSnapshot | null>(null);
  readonly error = signal<string | null>(null);
  readonly saving = signal(false);
  readonly limits = signal<QuotaLimits>(NO_LIMITS);
  readonly overrideReason = signal('');
  readonly formatUsd = formatUsdExact;
  /** Header state: the override, else the status; a scope without any limit reads "No limit". */
  readonly stateLabel = computed(() => {
    const quota = this.snapshot();
    if (!quota) return null;
    if (quota.overrideActive) return 'Owner override active';
    return !quota.limits && quota.status === 'ok' ? 'No limit' : STATE_LABELS[quota.status] ?? quota.status;
  });
  /** One row per UTC day of the quota week, dated from the week start. */
  readonly trend = computed(() => {
    const quota = this.snapshot();
    const weekStart = quota ? Date.parse(quota.weekStartUtc) : Number.NaN;
    if (!quota || Number.isNaN(weekStart)) return [];
    return quota.dailyTrend.map((day, index) => ({ ...day, date: new Date(weekStart + index * DAY_MS).toISOString() }));
  });
  /** Bumped by every refresh; an answer whose number is no longer current is dropped. */
  private refreshSequence = 0;

  constructor() {
    effect(() => {
      this.workspaceId(); this.projectId();
      // A new scope never shows or saves the previous scope's numbers.
      untracked(() => {
        this.snapshot.set(null);
        this.limits.set(NO_LIMITS);
        this.error.set(null);
        this.refresh();
      });
    });
  }

  private url(): string {
    const base = `/api/workspaces/${encodeURIComponent(this.workspaceId())}`;
    return this.projectId() ? `${base}/projects/${encodeURIComponent(this.projectId()!)}/quota` : `${base}/quota`;
  }

  refresh(): void {
    const sequence = ++this.refreshSequence;
    const url = this.url();
    // A later refresh, or a workspace/project input change not yet seen by the effect, supersedes this answer.
    const current = () => sequence === this.refreshSequence && url === this.url();
    this.http.get<QuotaSnapshot>(url).subscribe({
      next: result => {
        if (!current()) return;
        this.snapshot.set(result);
        this.limits.set(result.limits ?? NO_LIMITS);
        this.error.set(null);
      },
      error: () => { if (current()) this.error.set('Could not load quota usage.'); },
    });
  }

  update(field: keyof QuotaLimits, value: string | number | null): void {
    this.limits.update(current => ({ ...current, [field]: value == null || value === '' ? null : Number(value) }));
  }

  save(): void {
    this.saving.set(true);
    this.http.put<void>(this.url(), this.limits()).subscribe({
      next: () => { this.saving.set(false); this.refresh(); },
      error: () => { this.saving.set(false); this.error.set('Could not save quota. Check the values and owner access.'); },
    });
  }

  override(): void {
    if (!this.overrideReason().trim()) return;
    this.saving.set(true);
    this.http.post<void>(`${this.url()}/override`, { reason: this.overrideReason().trim() }).subscribe({
      next: () => { this.saving.set(false); this.overrideReason.set(''); this.refresh(); },
      error: () => { this.saving.set(false); this.error.set('Could not record the override.'); },
    });
  }
}

import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { AuthSessionState } from '../../../../services/auth.service';

interface QuotaLimits {
  modelTokensPerWeek: number | null;
  computeCoreHoursPerWeek: number | null;
  hostingSlots: number | null;
}
interface QuotaUsage { modelTokens: number; modelCostUsd: number; computeCoreHours: number; hostingSlots: number }
interface QuotaConsumer { projectId: string; project: string; cardId: string | null; modelTokens: number; computeCoreHours: number }
interface QuotaProjectConsumer { projectId: string; project: string; modelTokens: number; computeCoreHours: number }
interface QuotaSnapshot {
  workspaceId: string; projectId: string | null; weekEndUtc: string;
  limits: QuotaLimits | null; usage: QuotaUsage; remaining: QuotaUsage;
  status: string; biggestConsumers: QuotaConsumer[]; biggestProjects: QuotaProjectConsumer[]; dailyTrend: QuotaUsage[];
  meterComplete: boolean; overrideActive: boolean; codingAllowed: boolean; hostingAllowed: boolean; message: string | null;
}

@Component({
  selector: 'app-workspace-quota',
  standalone: true,
  imports: [CommonModule, FormsModule],
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
  readonly limits = signal<QuotaLimits>({ modelTokensPerWeek: null, computeCoreHoursPerWeek: null, hostingSlots: null });
  readonly overrideReason = signal('');

  constructor() {
    effect(() => {
      this.workspaceId(); this.projectId();
      untracked(() => this.refresh());
    });
  }

  private url(): string {
    const base = `/api/workspaces/${encodeURIComponent(this.workspaceId())}`;
    return this.projectId() ? `${base}/projects/${encodeURIComponent(this.projectId()!)}/quota` : `${base}/quota`;
  }

  refresh(): void {
    this.http.get<QuotaSnapshot>(this.url()).subscribe({
      next: result => {
        this.snapshot.set(result);
        this.limits.set(result.limits ?? { modelTokensPerWeek: null, computeCoreHoursPerWeek: null, hostingSlots: null });
        this.error.set(null);
      },
      error: () => this.error.set('Could not load quota usage.'),
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

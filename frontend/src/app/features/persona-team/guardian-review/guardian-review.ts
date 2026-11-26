import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RegistryProjectSummary } from '../../../models/task.model';
import { AuthService } from '../../../services/auth.service';

interface Criterion {
  id: string; label: string; unit: string; target: string; weight: number;
  applied: number | null; guardian: number | null; appliedScore: number | null; guardianScore: number | null;
  provenance: string[]; window: string; note: string;
}
interface Review {
  project: string; template: string; from: string; to: string; live: boolean; shadowSince: string | null;
  shadowWeekComplete: boolean; criteria: Criterion[]; appliedValue: number | null; guardianValue: number | null;
  disagreements: { id: string; test: string; at: string; guardian: string; applied: string; outcome: string | null; provenance: string[] }[];
  missingSources: string[];
}
@Component({ selector: 'app-guardian-review', standalone: true, imports: [DatePipe, DecimalPipe],
  templateUrl: './guardian-review.html', styleUrl: './guardian-review.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class GuardianReviewComponent {
  readonly projects = input<readonly RegistryProjectSummary[]>([]);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly project = signal('');
  readonly review = signal<Review | null>(null);
  readonly weights = signal<Record<string, number>>({});
  readonly week = signal('');
  readonly error = signal('');
  readonly loading = signal(false);
  readonly busy = signal(false);
  private request = 0;
  readonly owner = computed(() => this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner');
  constructor() { effect(() => { const projects = this.projects();
    if (!projects.some(p => p.id === this.project())) { this.project.set(projects[0]?.id ?? ''); this.load(); }
  }); }
  selectProject(id: string): void { this.project.set(id); this.load(); }
  load(): void {
    const request = ++this.request; this.review.set(null);
    if (!this.project()) { this.loading.set(false); return; }
    this.loading.set(true); this.error.set('');
    const project = this.project();
    const query = this.week() ? `?week=${encodeURIComponent(this.week() + 'T00:00:00Z')}` : '';
    this.http.get<Review>(`/api/projects/${project}/line-guardian/review${query}`).subscribe({
      next: review => { if (request !== this.request) return;
        this.review.set(review); this.weights.set(Object.fromEntries(review.criteria.map(c => [c.id, c.weight]))); this.loading.set(false); },
      error: () => { if (request !== this.request) return; this.error.set('Guardian review could not be loaded. Choose a Monday in UTC and retry.'); this.loading.set(false); },
    });
  }
  editWeight(id: string, value: string): void { this.weights.update(weights => ({ ...weights, [id]: Number(value) })); }
  saveWeights(): void {
    this.busy.set(true); this.error.set('');
    this.http.put(`/api/projects/${this.project()}/line-guardian/review/weights`, this.weights()).subscribe({
      next: () => { this.busy.set(false); this.load(); }, error: () => { this.busy.set(false); this.error.set('Weights could not be saved. Use six non-negative weights with a positive total.'); },
    });
  }
  switchMode(live: boolean): void {
    this.busy.set(true); this.error.set('');
    this.http.put(`/api/projects/${this.project()}/line-guardian/mode`, { live }).subscribe({
      next: () => { this.busy.set(false); this.load(); }, error: () => { this.busy.set(false); this.error.set('Guardian mode could not be changed. Owner access is required.'); },
    });
  }
  exportReport(): void {
    const report = this.review(); if (!report) return;
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob); const link = document.createElement('a');
    link.href = url; link.download = `guardian-v1-${report.project}-${report.from.slice(0, 10)}.json`; link.click(); URL.revokeObjectURL(url);
  }
}

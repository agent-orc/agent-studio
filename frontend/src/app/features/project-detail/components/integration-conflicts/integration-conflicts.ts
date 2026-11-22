import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe, DecimalPipe } from '@angular/common';

export interface IntegrationConflictSummary {
  conflicts: number;
  integratedCards: number;
  per100IntegratedCards: number | null;
  topFiles: { name: string; count: number }[];
  cardPairs: { cardA: string; cardB: string; count: number }[];
  daily: { day: string; conflicts: number; integratedCards: number; per100IntegratedCards: number | null }[];
}
interface ConflictResponse { summary: IntegrationConflictSummary }

/** Selection evidence: conflict frequency and shared files, including recovery routes. */
@Component({
  selector: 'app-integration-conflicts',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './integration-conflicts.html',
  styleUrl: './integration-conflicts.scss',
})
export class IntegrationConflictsComponent {
  readonly projectName = input.required<string>();
  readonly summary = signal<IntegrationConflictSummary | null>(null);
  readonly loading = signal(true);
  readonly error = signal(false);
  private readonly refresh = signal(0);
  private readonly http = inject(HttpClient);

  constructor() {
    effect(onCleanup => {
      this.refresh();
      const project = this.projectName();
      this.loading.set(true);
      this.error.set(false);
      this.summary.set(null);
      const to = new Date();
      const from = new Date(to.getTime() - 7 * 86_400_000);
      const request = untracked(() => this.http.get<ConflictResponse>(`/api/projects/${encodeURIComponent(project)}/integration-conflicts`, {
        params: { from: from.toISOString(), to: to.toISOString() },
      }).subscribe({
        next: response => { this.summary.set(response.summary); this.loading.set(false); },
        error: () => { this.error.set(true); this.loading.set(false); },
      }));
      onCleanup(() => request.unsubscribe());
    });
  }

  retry(): void { this.refresh.update(value => value + 1); }
}

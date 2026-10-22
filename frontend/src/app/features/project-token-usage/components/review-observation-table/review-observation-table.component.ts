import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { ReviewObservationReport } from '../../models/project-token-usage.model';

@Component({
  selector: 'app-review-observation-table',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './review-observation-table.component.html',
  styleUrl: './review-observation-table.component.scss',
})
export class ReviewObservationTableComponent {
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly report = signal<ReviewObservationReport | null>(null);
  readonly error = signal<string | null>(null);
  readonly exportHref = () => `/api/projects/${encodeURIComponent(this.projectName())}/token-usage/review-observations/export`;

  constructor() {
    effect((onCleanup) => {
      const project = this.projectName();
      this.report.set(null);
      this.error.set(null);
      const request = this.tasks.getProjectReviewObservations(project).subscribe({
        next: report => this.report.set(report),
        error: () => this.error.set('Review observations could not be loaded.'),
      });
      onCleanup(() => request.unsubscribe());
    });
  }

  money(value: number | null): string {
    return value === null ? 'Unknown' : `$${value.toFixed(4)}`;
  }

  percent(value: number | null): string {
    return value === null ? 'Unknown' : `${(value * 100).toFixed(1)}%`;
  }
}

import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { TaskExecutionLocation } from '../../../../models/task.model';
import { ExecutionLocationBadgeComponent } from '../../../../components/execution-location-badge/execution-location-badge.component';

@Component({
  selector: 'app-run-disposition-badges',
  standalone: true,
  imports: [ExecutionLocationBadgeComponent],
  templateUrl: './run-disposition-badges.component.html',
  styleUrl: './run-disposition-badges.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunDispositionBadgesComponent {
  readonly execution = input<TaskExecutionLocation | null>(null);
  readonly state = input.required<string>();
  readonly olderBriefPending = input(false);
}

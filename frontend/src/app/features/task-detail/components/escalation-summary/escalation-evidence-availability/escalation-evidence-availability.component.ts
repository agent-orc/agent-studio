import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskInfo } from '../../../../../models/task.model';
import { resolveTaskArtifactLink } from '../../task-artifact-links/task-artifact-link';
import type { BatchDiagnostic } from '../escalation-summary.util';

/** The batch artifact and an explicit inventory when review findings are absent. */
@Component({
  selector: 'app-escalation-evidence-availability',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './escalation-evidence-availability.component.html',
  styleUrl: './escalation-evidence-availability.component.scss',
})
export class EscalationEvidenceAvailabilityComponent {
  readonly job = input.required<TaskInfo>();
  readonly batch = input<BatchDiagnostic | null>(null);
  readonly showAvailability = input(false);
  readonly hasReviewArtifacts = input(false);
  readonly hasDelivery = input(false);
  readonly artifactUrl = computed(() => resolveTaskArtifactLink(
    'results/batch-diagnostic.json', {
      jobId: this.job().id, watchPath: this.job().watchPath,
    })?.href ?? null);
}

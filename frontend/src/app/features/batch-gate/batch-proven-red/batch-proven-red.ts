import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { batchIsolationEvidenceUrl, type BatchIsolationFinding } from '../../../models/batch-gate.model';

/**
 * AGT-3577: why the bisection proved a member red: the failing tests and
 * error excerpts of the run that proved it, and that run's evidence.
 */
@Component({
  selector: 'app-batch-proven-red',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-proven-red.html',
  styleUrl: './batch-proven-red.scss',
})
export class BatchProvenRedComponent {
  readonly project = input.required<string>();
  readonly finding = input.required<BatchIsolationFinding>();
  readonly failures = computed(() => this.finding().failures ?? []);
  readonly evidenceUrl = computed(() => batchIsolationEvidenceUrl(this.project(), this.finding()));
}

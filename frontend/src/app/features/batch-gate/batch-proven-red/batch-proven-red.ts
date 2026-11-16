import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../components/disclosure-marker/disclosure-marker.component';
import { batchIsolationEvidenceUrl, type BatchIsolationFinding } from '../../../models/batch-gate.model';

/**
 * Short name of an evidence file: its folder and file name. The folder carries
 * the batch id, so two findings' evidence stays apart; the host prefix does not.
 */
export function shortEvidenceName(path: string): string {
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.slice(-2).join('/') || path;
}

/**
 * AGT-3577: why the bisection proved a member red: the failing tests and
 * error excerpts of the run that proved it, and that run's evidence.
 */
@Component({
  selector: 'app-batch-proven-red',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DisclosureMarkerComponent],
  templateUrl: './batch-proven-red.html',
  styleUrl: './batch-proven-red.scss',
})
export class BatchProvenRedComponent {
  readonly project = input.required<string>();
  readonly finding = input.required<BatchIsolationFinding>();
  readonly failures = computed(() => this.finding().failures ?? []);
  readonly evidenceUrl = computed(() => batchIsolationEvidenceUrl(this.project(), this.finding()));
  /** AGT-3836: the absolute host path wrapped over five lines at 390 px; it now shows on demand. */
  readonly evidenceName = computed(() => shortEvidenceName(this.finding().evidencePath));
  readonly fullPathShown = signal(false);
}

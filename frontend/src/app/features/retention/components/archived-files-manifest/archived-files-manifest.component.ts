import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { formatRetentionBytes, type RetentionArchiveManifest } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Cold archive manifest state to status tone (AGT-3403); unknown states are neutral. */
const MANIFEST_STATE_TONES: Readonly<Record<string, StatusTone>> = {
  cold: 'info',
  archived: 'info',
  'hot-restored': 'success',
  restored: 'success',
};

export function archiveManifestStateTone(state: string | null | undefined): StatusTone {
  return MANIFEST_STATE_TONES[state ?? ''] ?? 'neutral';
}

@Component({
  selector: 'app-archived-files-manifest',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './archived-files-manifest.component.html',
  styleUrl: './archived-files-manifest.component.scss',
})
export class ArchivedFilesManifestComponent {
  readonly jobId = input<string | null>(null);
  readonly archived = input(false);
  private readonly retention = inject(RetentionService);
  readonly manifest = signal<RetentionArchiveManifest | null>(null);
  readonly bytes = formatRetentionBytes;
  readonly stateTone = archiveManifestStateTone;

  constructor() {
    effect(() => {
      const jobId = this.jobId();
      if (!jobId || !this.archived() || this.retention.restoredTaskId() === jobId) {
        this.manifest.set(null);
        return;
      }
      this.retention.getManifest(jobId).subscribe({
        next: manifest => this.manifest.set(manifest),
        error: () => this.manifest.set(null),
      });
    }, { allowSignalWrites: true });
  }
}

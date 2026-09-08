import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { formatRetentionBytes, type RetentionArchiveManifest } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';

@Component({
  selector: 'app-archived-files-manifest',
  standalone: true,
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

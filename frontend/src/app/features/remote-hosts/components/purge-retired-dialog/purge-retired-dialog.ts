import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import type { PurgeRetiredClientResult } from '../../models/remote-host.model';

/**
 * "Delete retired…" bulk cleanup dialog (AGT-2748). A name-prefix filter
 * (defaults to `e2e-`, the leftover convention every e2e fixture already
 * uses) previews the exact retired identities a purge would delete before
 * the operator commits. Runners with an active lease are always skipped,
 * dry-run or not.
 */
@Component({
  selector: 'app-purge-retired-dialog',
  standalone: true,
  templateUrl: './purge-retired-dialog.html',
  styleUrl: './purge-retired-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PurgeRetiredDialogComponent {
  private readonly service = inject(RemoteHostsService);
  readonly cancelled = output<void>();
  readonly purged = output<number>();

  readonly prefix = signal('e2e-');
  readonly previewed = signal(false);

  readonly purging = this.service.purging;
  readonly purgeError = this.service.purgeError;
  readonly preview = this.service.purgePreview;

  readonly results = computed<readonly PurgeRetiredClientResult[]>(() => this.preview()?.results ?? []);
  readonly deletableCount = computed(() =>
    this.results().filter(row => row.outcome === 'would-delete').length);
  readonly deletedCount = computed(() =>
    this.results().filter(row => row.outcome === 'deleted').length);
  readonly applied = computed(() => this.preview()?.dryRun === false);

  runPreview(): void {
    this.previewed.set(true);
    this.service.previewPurgeRetired(this.prefix().trim());
  }

  confirmPurge(): void {
    this.service.applyPurgeRetired(this.prefix().trim());
  }

  editPrefix(): void {
    this.previewed.set(false);
    this.service.clearPurgePreview();
  }

  close(): void {
    const deleted = this.deletedCount();
    this.service.clearPurgePreview();
    if (deleted > 0) this.purged.emit(deleted);
    else this.cancelled.emit();
  }

  outcomeLabel(outcome: PurgeRetiredClientResult['outcome']): string {
    switch (outcome) {
      case 'would-delete': return 'Would delete';
      case 'deleted': return 'Deleted';
      case 'skipped-active-lease': return 'Skipped · active lease';
      case 'skipped-not-retired': return 'Skipped · no longer retired';
    }
  }
}

import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { formatBytes } from '../../models/task-server.model';
import { TaskServerService } from '../../services/task-server.service';

@Component({
  selector: 'app-archive-retention-card',
  standalone: true,
  templateUrl: './archive-retention-card.html',
  styleUrl: './archive-retention-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ArchiveRetentionCardComponent {
  private readonly service = inject(TaskServerService);
  readonly target = this.service.archiveTarget;
  readonly plan = this.service.archiveDeletionPlan;
  readonly busy = this.service.archiveBusy;
  readonly confirmation = signal('');
  readonly deletionActions = computed(() => this.plan()?.actions ?? []);
  readonly deletionBytes = computed(() => this.deletionActions().reduce((sum, action) => sum + action.bytes, 0));
  readonly canConfirm = computed(() =>
    this.confirmation() === 'DELETE ARCHIVED PAYLOADS' && this.deletionActions().length > 0 && !this.busy());
  readonly formatBytes = formatBytes;

  preview(): void { this.confirmation.set(''); void this.service.previewArchiveDeletion(); }
  cancel(): void { this.confirmation.set(''); this.service.cancelArchiveDeletion(); }
  confirm(): void { if (this.canConfirm()) void this.service.confirmArchiveDeletion(); }
  updateConfirmation(event: Event): void { this.confirmation.set((event.target as HTMLInputElement).value); }
}

import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { formatRetentionBytes, type RetentionArchiveManifest } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';

@Component({
  selector: 'app-archived-task-notice',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './archived-task-notice.component.html',
  styleUrl: './archived-task-notice.component.scss',
})
export class ArchivedTaskNoticeComponent {
  readonly taskId = input.required<string>();
  readonly lane = input.required<string>();
  readonly archiveState = input<string | null>(null);
  readonly restored = output<void>();

  private readonly retention = inject(RetentionService);
  readonly manifest = signal<RetentionArchiveManifest | null>(null);
  readonly restoring = signal(false);
  readonly restoreError = signal<string | null>(null);
  readonly bytes = formatRetentionBytes;

  constructor() {
    effect(() => {
      const taskId = this.taskId();
      if (this.archiveState() !== 'cold' && this.lane() !== '7-archive') {
        this.manifest.set(null);
        return;
      }
      this.retention.getManifest(taskId).subscribe({
        next: manifest => this.manifest.set(manifest.state === 'hot-restored' ? null : manifest),
        error: () => this.manifest.set(null),
      });
    }, { allowSignalWrites: true });
  }

  async restore(): Promise<void> {
    const manifest = this.manifest();
    if (!manifest || this.restoring()) return;
    this.restoring.set(true);
    this.restoreError.set(null);
    try {
      await firstValueFrom(this.retention.restoreTask(this.taskId()));
      this.manifest.set(null);
      this.restored.emit();
    } catch {
      this.restoreError.set('The archived task could not be restored.');
    } finally {
      this.restoring.set(false);
    }
  }
}

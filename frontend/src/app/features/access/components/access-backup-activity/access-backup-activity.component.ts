import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { backupOperationLabel, backupOutcomeLabel, backupOutcomeTone } from '../../models/backup-activity.model';
import { formatAuditTime } from '../../models/operator-agent.model';
import { BackupActivityService } from '../../services/backup-activity.service';

/**
 * Who created, copied, verified, pruned, decrypted, or restored backups, and
 * with what outcome: the backup records of the control plane audit chain.
 * The parent loads them; the section shows only after the server answered as
 * for the Owner.
 */
@Component({
  selector: 'app-access-backup-activity',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-backup-activity.component.html',
  styleUrl: './access-backup-activity.component.scss',
})
export class AccessBackupActivityComponent {
  readonly backups = inject(BackupActivityService);
  readonly entries = computed(() => this.backups.activity()?.entries ?? []);
  readonly time = formatAuditTime;
  readonly operation = backupOperationLabel;
  readonly outcome = backupOutcomeLabel;
  readonly tone = backupOutcomeTone;
}

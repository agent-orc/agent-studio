import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';

/**
 * AGT-3076: a Human Review card whose run attempt the attempt authority store
 * no longer holds (store rebuilt into a new authority epoch) can only be
 * accepted with a written operator override. The detail says so before the
 * operator tries to accept.
 */
@Component({
  selector: 'app-acceptance-attempt-gap-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './acceptance-attempt-gap-notice.component.html',
  styleUrl: './acceptance-attempt-gap-notice.component.scss',
})
export class AcceptanceAttemptGapNoticeComponent {
  readonly job = input.required<TaskInfo>();

  readonly gap = computed(() => this.job().acceptanceAttemptGap ?? null);
}

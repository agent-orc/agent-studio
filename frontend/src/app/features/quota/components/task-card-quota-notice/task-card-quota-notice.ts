import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { WorkspaceQuotaBoardState } from '../../state/workspace-quota-board-state.service';

/** Card-level quota refusal: shown only on cards that still wait for admission. */
@Component({
  selector: 'app-task-card-quota-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-card-quota-notice.html',
  styleUrl: './task-card-quota-notice.scss',
})
export class TaskCardQuotaNoticeComponent {
  private readonly quotaBoard = inject(WorkspaceQuotaBoardState);
  readonly projectName = input.required<string>();
  readonly active = input(true);
  readonly message = computed(() => this.active() ? this.quotaBoard.messageFor(this.projectName()) : null);
}

import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { WorkspaceQuotaBoardState } from '../../state/workspace-quota-board-state.service';

/** Studio-header line for a workspace quota at the soft warning, the hard stop, or with every hosting slot in use. */
@Component({
  selector: 'app-workspace-quota-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-quota-banner.html',
  styleUrl: './workspace-quota-banner.scss',
})
export class WorkspaceQuotaBannerComponent {
  private readonly board = inject(WorkspaceQuotaBoardState);
  readonly quota = computed(() => {
    const quota = this.board.workspaceQuota();
    return quota && quota.status !== 'ok' && quota.message && !quota.overrideActive ? quota : null;
  });
}

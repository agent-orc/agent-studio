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
    const scopes = [quota, ...this.board.projectQuotas()].filter(scope =>
      scope && scope.status !== 'ok' && scope.message && !scope.overrideActive);
    if (!scopes.length) return null;
    return { status: scopes.some(scope => scope?.status === 'blocked') ? 'blocked' : scopes[0]!.status,
      message: scopes.map(scope => `${scope!.project ?? 'Workspace'}: ${scope!.message}`).join(' ') };
  });
}

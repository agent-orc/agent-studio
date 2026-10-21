import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';

import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
// Direct path (not the quota barrel) so the deferred editor stays a lazy chunk.
import { WorkspaceQuotaComponent } from '../workspace-quota/workspace-quota.component';

/** Settings list of workspace quotas: one toggle per workspace opens that workspace's quota editor. */
@Component({
  selector: 'app-workspace-quota-list',
  standalone: true,
  imports: [DisclosureMarkerComponent, WorkspaceQuotaComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-quota-list.html',
  styleUrl: './workspace-quota-list.scss',
})
export class WorkspaceQuotaListComponent {
  readonly workspaces = input.required<readonly Pick<RegistryWorkspaceListItem, 'id' | 'displayName'>[]>();
  readonly activeWorkspaceId = signal<string | null>(null);

  toggle(workspaceId: string): void {
    this.activeWorkspaceId.update(current => (current === workspaceId ? null : workspaceId));
  }
}

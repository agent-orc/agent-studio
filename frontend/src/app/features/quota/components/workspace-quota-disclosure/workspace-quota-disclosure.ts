import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';

import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { WorkspaceQuotaComponent } from '../workspace-quota/workspace-quota.component';

/** One workspace's quota editor behind a disclosure row; the editor loads on first open (initial bundle budget). */
@Component({
  selector: 'app-workspace-quota-disclosure',
  standalone: true,
  imports: [DisclosureMarkerComponent, WorkspaceQuotaComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-quota-disclosure.html',
  styleUrl: './workspace-quota-disclosure.scss',
})
export class WorkspaceQuotaDisclosureComponent {
  readonly workspaceId = input.required<string>();
  readonly workspaceName = input.required<string>();
  readonly open = signal(false);
}

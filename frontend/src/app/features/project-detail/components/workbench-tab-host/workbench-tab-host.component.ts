import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import type { WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import { WorkbenchOverviewComponent } from '../workbench-overview/workbench-overview.component';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';

/**
 * Lazy boundary for repository document tabs. These views are opened rarely
 * compared with the board, so their viewer and decision UI stay out of the
 * initial application bundle while preserving the existing tab contract.
 */
@Component({
  selector: 'app-workbench-tab-host',
  standalone: true,
  imports: [LoadingSurfaceComponent, WorkbenchOverviewComponent, WorkbenchViewerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-tab-host.component.html',
  styleUrl: './workbench-tab-host.component.scss',
})
export class WorkbenchTabHostComponent {
  readonly mode = input.required<'overview' | 'viewer'>();
  readonly projectName = input<string | null>(null);
  readonly projectId = input<string | null>(null);
  readonly workbenchId = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchOverviewItem>();
  readonly openWiki = output<string>();
}

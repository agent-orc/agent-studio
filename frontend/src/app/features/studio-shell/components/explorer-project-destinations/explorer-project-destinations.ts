import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { ExplorerLaneDashboardComponent } from '../explorer-lane-dashboard/explorer-lane-dashboard.component';
import { ExplorerWorkbenchListComponent } from '../explorer-workbench-list/explorer-workbench-list.component';
import { QualityStudioExplorerRowComponent } from '../quality-studio-explorer-row/quality-studio-explorer-row.component';
import { BOARD_ACTIVE_WORK_TOOLTIP, boardLaneCountsLabel, laneCountsFor } from '../../studio-shell.project-rows';
import type { ExplorerProjectNode } from '../explorer-workspace-tree/explorer-workspace-groups';
import type { ExplorerProjectSurface } from '../explorer-workspace-tree/explorer-workspace-tree.component';
import type { ExplorerTreeMetricView } from '../explorer-lane-dashboard/explorer-lane-dashboard.component';
import type { WorkbenchListItem } from '../../../../models/project-docs.model';

export type ProjectDestination = 'board' | 'workbenches' | 'deploy' | 'operations' | 'wiki' | 'hub' | 'settings';

/** The Explorer's project destinations share one nesting and active-state contract. */
@Component({
  selector: 'app-explorer-project-destinations',
  standalone: true,
  imports: [TreeRowComponent, ExplorerLaneDashboardComponent, ExplorerWorkbenchListComponent, QualityStudioExplorerRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer-project-destinations.html',
})
export class ExplorerProjectDestinationsComponent {
  readonly project = input.required<ExplorerProjectNode>();
  readonly activeSurface = input<ExplorerProjectSurface | null>(null);
  readonly activeWorkbenchId = input<string | null>(null);
  readonly metricView = input.required<ExplorerTreeMetricView>();
  readonly navigate = output<ProjectDestination>();
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly boardActiveWorkTooltip = BOARD_ACTIVE_WORK_TOOLTIP;
  readonly laneCountsFor = laneCountsFor;
  readonly boardLaneCountsLabel = boardLaneCountsLabel;
}

import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  WORKBENCH_SORT_OPTIONS,
  type WorkbenchSortDirection,
  type WorkbenchSortKey,
  type WorkbenchReviewFilter,
  type WorkbenchCreatedFilter,
} from '../workbench-overview/workbench-overview-view-state.service';

@Component({
  selector: 'app-workbench-overview-controls',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-controls.component.html',
  styleUrl: './workbench-overview-controls.component.scss',
})
export class WorkbenchOverviewControlsComponent {
  readonly query = input.required<string>();
  readonly sortKey = input.required<WorkbenchSortKey>();
  readonly direction = input.required<WorkbenchSortDirection>();
  readonly active = input.required<boolean>();
  readonly reviewFilter = input.required<WorkbenchReviewFilter>();
  readonly createdFilter = input.required<WorkbenchCreatedFilter>();
  readonly queryChange = output<string>();
  readonly sortSelectionChange = output<WorkbenchSortKey>();
  readonly directionToggle = output<void>();
  readonly resetView = output<void>();
  readonly reviewFilterChange = output<WorkbenchReviewFilter>();
  readonly createdFilterChange = output<WorkbenchCreatedFilter>();
  readonly sortOptions = WORKBENCH_SORT_OPTIONS;

  onQueryInput(event: Event): void {
    this.queryChange.emit((event.target as HTMLInputElement).value);
  }

  onReviewFilter(event: Event): void {
    this.reviewFilterChange.emit((event.target as HTMLSelectElement).value as WorkbenchReviewFilter);
  }

  onCreatedFilter(event: Event): void {
    this.createdFilterChange.emit((event.target as HTMLSelectElement).value as WorkbenchCreatedFilter);
  }

  onSortSelection(event: Event): void {
    this.sortSelectionChange.emit((event.target as HTMLSelectElement).value as WorkbenchSortKey);
  }

}

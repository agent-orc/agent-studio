import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  WORKBENCH_SORT_OPTIONS,
  type WorkbenchSortDirection,
  type WorkbenchSortKey,
  type WorkbenchReviewFilter,
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
  readonly queryChange = output<string>();
  readonly sortChange = output<Exclude<WorkbenchSortKey, 'default'>>();
  readonly resetView = output<void>();
  readonly reviewFilterChange = output<WorkbenchReviewFilter>();
  readonly sortOptions = WORKBENCH_SORT_OPTIONS;

  onQueryInput(event: Event): void {
    this.queryChange.emit((event.target as HTMLInputElement).value);
  }

  onReviewFilter(event: Event): void {
    this.reviewFilterChange.emit((event.target as HTMLSelectElement).value as WorkbenchReviewFilter);
  }

  sortAriaLabel(key: Exclude<WorkbenchSortKey, 'default'>, label: string): string {
    if (this.sortKey() !== key) return `Sort by ${label}`;
    const nextDirection = this.direction() === 'asc' ? 'descending' : 'ascending';
    return `Sorted by ${label}, ${this.direction() === 'asc' ? 'ascending' : 'descending'}. Sort ${nextDirection}`;
  }
}

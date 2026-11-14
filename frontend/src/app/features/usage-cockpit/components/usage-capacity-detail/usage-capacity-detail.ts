import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, input } from '@angular/core';

import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { clearVisibleInterval, setVisibleInterval } from '../../../../utils/visible-interval';
import { CliCatalogStore } from '../../../cli/catalog';
import type { CliType } from '../../../../models/task.model';
import { buildCapacityDetailView } from '../../capacity-detail.util';
import { OperatorCapacityService } from '../../services/operator-capacity.service';
import { USAGE_COCKPIT_REFRESH_MS } from '../../services/usage-cockpit.service';

/**
 * Capacity section of the usage detail (AGT-3584): Ready cards whose next
 * pickup would fall back, wait or be held at the current quota, from
 * `GET /api/operator/capacity`. It reads only while the detail is open.
 */
@Component({
  selector: 'app-usage-capacity-detail',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-capacity-detail.html',
  styleUrl: '../usage-detail-panel/usage-detail-panel.scss',
})
export class UsageCapacityDetailComponent implements OnDestroy {
  private readonly capacity = inject(OperatorCapacityService);
  private readonly catalogs = inject(CliCatalogStore);
  private readonly poll = setVisibleInterval(() => this.capacity.refresh(), USAGE_COCKPIT_REFRESH_MS);

  readonly sectionId = input.required<string>();
  readonly timeZone = input.required<string>();
  /** Reference time for countdowns, epoch milliseconds. */
  readonly now = input.required<number>();

  readonly failed = this.capacity.failed;

  readonly view = computed(() => {
    const response = this.capacity.response();
    if (!response) return null;
    return buildCapacityDetailView(response, this.timeZone(), this.now(), (cli, model) => {
      if (!cli || !model) return null;
      const entry = this.catalogs.modelsFor(cli as CliType).find(candidate => candidate.id === model);
      if (entry?.available !== false) return null;
      return entry.availabilityNote?.trim() || 'Not offered by the installed CLI.';
    });
  });

  constructor() {
    this.capacity.refresh();
  }

  ngOnDestroy(): void {
    clearVisibleInterval(this.poll);
  }
}

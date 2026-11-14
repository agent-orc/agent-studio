import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { SegmentedControlComponent, type SegmentedOption } from '../../../../components/segmented-control/segmented-control.component';
import { PendingButtonDirective } from '../../../../components/async-feedback/pending-button.directive';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { readinessKey } from '../../models/acceptance-readiness-groups.util';
import type { AcceptanceReadinessFilter } from '../../models/acceptance-readiness.model';
import { AcceptanceRoundDialogComponent } from '../acceptance-round-dialog/acceptance-round-dialog.component';

/**
 * AGT-3564 - the Human Review lane's readiness bar: how many cards the
 * completion would accept now, a filter by readiness, "Accept all ready"
 * through the normal batch move, and the per-card result of that accept.
 */
@Component({
  selector: 'app-acceptance-readiness-bar',
  standalone: true,
  imports: [StatusIndicatorComponent, SegmentedControlComponent, PendingButtonDirective, AcceptanceRoundDialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './acceptance-readiness-bar.html',
  styleUrl: './acceptance-readiness-bar.scss',
})
export class AcceptanceReadinessBarComponent {
  readonly store = inject(AcceptanceReadinessStore);
  private readonly confirm = inject(ConfirmDialogService);
  readonly jobs = input.required<readonly TaskInfo[]>();
  readonly mutationsBlocked = input(false);

  /** Cards the lane currently shows under the filter. */
  private readonly shown = computed(() => this.store.groupsFor(this.jobs())?.flatMap(group => group.jobs) ?? []);
  readonly readyShown = computed(() => this.store.readyAmong(this.shown()));
  readonly readyCount = computed(() => this.store.readyAmong(this.jobs()).length);
  readonly filterOptions = computed<readonly SegmentedOption<AcceptanceReadinessFilter>[]>(() => {
    const total = this.jobs().length;
    const ready = this.readyCount();
    return [
      { value: 'all', label: `All ${total}`, testid: 'acceptance-filter-all', tooltip: 'Every card, grouped by readiness' },
      { value: 'ready', label: `Ready ${ready}`, testid: 'acceptance-filter-ready', tooltip: 'Cards the completion accepts now' },
      { value: 'not-ready', label: `Not ready ${total - ready}`, testid: 'acceptance-filter-not-ready', tooltip: 'Cards that need a round, a deploy, or a decision' },
    ];
  });
  readonly acceptedCount = computed(() => this.store.results().filter(result => result.accepted).length);

  /** The lane's card set; a new card or a card that left starts a new check. */
  private readonly signature = computed(() => this.jobs().map(job => `${readinessKey(job)}@${job.lastActivity ?? ''}`).sort().join('|'));

  constructor() {
    effect(() => {
      this.signature();
      untracked(() => void this.store.refresh(this.jobs()));
    });
  }

  refresh(): void {
    void this.store.refresh(this.jobs());
  }

  setFilter(filter: AcceptanceReadinessFilter): void {
    this.store.filter.set(filter);
  }

  async acceptReady(): Promise<void> {
    const ready = this.readyShown();
    if (ready.length === 0) return;
    const confirmed = await this.confirm.confirm({
      kind: 'primary',
      title: `Accept ${ready.length} ready ${ready.length === 1 ? 'card' : 'cards'}?`,
      message: 'Each card goes through the normal completion and its full checks again. A card that no longer passes stays in the lane with its reason.',
      confirmLabel: `Accept ${ready.length}`,
    });
    if (confirmed) await this.store.acceptReady(ready, this.jobs());
  }

  dismissResults(): void {
    this.store.results.set([]);
  }
}

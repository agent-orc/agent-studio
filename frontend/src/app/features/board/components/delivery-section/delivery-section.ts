import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import {
  DELIVERY_COPY,
  deliveryHealthLabel,
  deliveryRunLabel,
} from '../../../../models/delivery-presentation';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { DeliveryColumnsService } from '../../../deployments/services/delivery-columns.service';
import { LaneCollapseService } from '../../state/lane-collapse.service';
import { TaskCardComponent } from '../task-card/task-card.component';
import { buildDeliveryGroup, type BoardDeliveryColumn } from '../board-lane-layout.util';

const FURTHEST_ONLY_KEY = 'deliveryFurthestOnly';

/**
 * The board's Delivery group after "Done & Decide" (AGT-3276 D5 L-B): one
 * column per environment from the `deploy/lanes` read model. Each column
 * header carries the release version, last run state and time, health and a
 * pending-approval pill; the body lists landed cards, then a "Waiting"
 * divider and the waiting cards. Tiles are the standard task card.
 *
 * The host is the board's lane-group container, so the shell owns focus and
 * sizing; columns collapse through {@link LaneCollapseService} like lanes.
 */
@Component({
  selector: 'app-delivery-section',
  standalone: true,
  imports: [DatePipe, DisclosureMarkerComponent, TaskCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './delivery-section.html',
  styleUrl: './delivery-section.scss',
  host: {
    '[style.display]': "group().columns.length ? null : 'none'",
    '[class.delivery--empty]': '!group().columns.length',
    '[style.flex-grow]': 'expandedColumns()',
  },
})
export class DeliverySectionComponent {
  private readonly source = inject(DeliveryColumnsService);
  private readonly collapse = inject(LaneCollapseService);

  /** Projects the board shows; one project per board tab, every project on the workspace board. */
  readonly projects = input<readonly string[]>([]);
  /** The filtered board feed; the section renders only cards it carries. */
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly mutationsBlocked = input(false);
  readonly jobClick = output<TaskInfo>();

  readonly copy = DELIVERY_COPY;
  readonly runLabel = deliveryRunLabel;
  readonly healthLabel = deliveryHealthLabel;
  readonly error = this.source.error;
  readonly furthestOnly = signal(readStoredFlag());
  readonly group = computed(() => buildDeliveryGroup(this.source.responses(), this.tasks(), this.furthestOnly()));
  readonly multiProject = computed(() => this.projects().length > 1);
  /** Grows the group like the lane groups: in proportion to its expanded columns. */
  readonly expandedColumns = computed(() => this.group().columns.filter((column) => !this.collapsed(column)).length);

  constructor() {
    effect(() => this.source.watch(this.projects()));
    inject(DestroyRef).onDestroy(() => this.source.watch([]));
  }

  toggleFurthestOnly(): void {
    const next = !this.furthestOnly();
    this.furthestOnly.set(next);
    try {
      localStorage.setItem(FURTHEST_ONLY_KEY, String(next));
    } catch (error) {
      console.warn('Delivery display option not persisted', error);
    }
  }

  collapsed(column: BoardDeliveryColumn): boolean {
    return this.collapse.isLaneCollapsed(collapseKey(column));
  }

  toggle(column: BoardDeliveryColumn): void {
    this.collapse.toggleLaneCollapse(collapseKey(column));
  }
}

function collapseKey(column: BoardDeliveryColumn): string {
  return `delivery:${column.id}`;
}

function readStoredFlag(): boolean {
  try {
    return localStorage.getItem(FURTHEST_ONLY_KEY) === 'true';
  } catch {
    return false;
  }
}

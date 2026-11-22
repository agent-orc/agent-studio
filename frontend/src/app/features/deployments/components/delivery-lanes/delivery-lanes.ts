import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { catchError, forkJoin, map, of, switchMap, timer } from 'rxjs';
import { BoardFiltersService, flattenGrouped } from '../../../board';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';

interface DeliveryCard { key: string; title: string; releaseVersion: string | null; deployedAtUtc: string | null }
interface DeliveryLane { environmentId: string | null; name: string; order: number; cards: DeliveryCard[] }
interface DeliveryProject { projectId: string; lanes: DeliveryLane[] }

/** Cards a column shows before its "Show all" control (AGT-3949). */
export const DELIVERY_LANE_PREVIEW = 12;

@Component({
  selector: 'app-delivery-lanes',
  standalone: true,
  imports: [DatePipe, DisclosureMarkerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './delivery-lanes.html',
  styleUrl: './delivery-lanes.scss',
})
export class DeliveryLanesComponent {
  private readonly http = inject(HttpClient);
  private readonly filters = inject(BoardFiltersService);
  readonly projects = input<readonly string[]>([]);
  readonly project = input<string | null>(null);
  readonly visibleKeys = input<ReadonlySet<string> | null>(null);
  readonly heading = input('Delivery');
  readonly boardFilters = input(false);
  readonly openDeployment = output<string>();
  readonly openCard = output<string>();
  readonly data = signal<DeliveryProject[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);
  private readonly filteredKeys = computed<ReadonlySet<string> | null>(() => {
    if (!this.boardFilters() || !this.filters.hasActiveFiltersOrSearch()) return null;
    return new Set(flattenGrouped(this.filters.filteredGrouped())
      .map(task => (task.displayKey ?? task.taskKey).toLowerCase()));
  });
  /** Columns the reader opened in full, as `project/environment` keys. */
  private readonly expanded = signal<ReadonlySet<string>>(new Set());
  /**
   * Per column: `cards` are all cards that pass the filters (the header
   * counts them), `shown` the ones rendered. A long column renders its first
   * {@link DELIVERY_LANE_PREVIEW} cards until the reader asks for all of them.
   */
  readonly rows = computed(() => this.data().map(project => ({
    ...project,
    lanes: project.lanes.map(lane => {
      const keys = this.visibleKeys() ?? this.filteredKeys();
      const cards = lane.cards.filter(card => keys === null || keys.has(card.key.toLowerCase()));
      const id = `${project.projectId}/${lane.environmentId ?? 'waiting'}`;
      const long = cards.length > DELIVERY_LANE_PREVIEW;
      const open = long && this.expanded().has(id);
      return { ...lane, id, cards, long, open, shown: long && !open ? cards.slice(0, DELIVERY_LANE_PREVIEW) : cards };
    }),
  })));
  readonly preview = DELIVERY_LANE_PREVIEW;

  constructor() {
    effect(onCleanup => {
      const project = this.project();
      const all = project ? [project] : this.projects();
      const names = this.boardFilters() && this.filters.hasExplicitProjectFilter()
        ? all.filter(name => this.filters.activeProjects().has(name)) : all;
      if (!names.length) { this.data.set([]); return; }
      this.loading.set(true);
      this.error.set(false);
      const subscription = timer(0, 15_000).pipe(
        switchMap(() => forkJoin(names.map(name => this.http.get<DeliveryProject>(
          `/api/projects/${encodeURIComponent(name)}/deploy/delivery-lanes`,
        ).pipe(catchError(() => of(null)))))),
        map(rows => rows.filter((row): row is DeliveryProject => row !== null)),
      ).subscribe(rows => {
        this.data.set(rows);
        this.error.set(rows.length !== names.length);
        this.loading.set(false);
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  toggleLane(id: string): void {
    this.expanded.update(current => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  openEnvironment(projectId: string, environmentId: string): void {
    this.openDeployment.emit(projectId);
    const selector = `[data-testid="deploy-environment-${CSS.escape(environmentId)}"]`;
    const focus = () => {
      const card = document.querySelector<HTMLElement>(selector);
      if (!card) return false;
      card.tabIndex = -1;
      card.focus({ preventScroll: true });
      card.scrollIntoView({ block: 'nearest' });
      return true;
    };
    if (focus()) return;
    const observer = new MutationObserver(() => { if (focus()) observer.disconnect(); });
    observer.observe(document.body, { childList: true, subtree: true });
    setTimeout(() => observer.disconnect(), 5_000);
  }
}

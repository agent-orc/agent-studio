import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { catchError, forkJoin, map, of, switchMap, timer } from 'rxjs';
import { BoardFiltersService, flattenGrouped } from '../../../board';

interface DeliveryCard { key: string; title: string; releaseVersion: string | null; deployedAtUtc: string | null }
interface DeliveryLane { environmentId: string | null; name: string; order: number; cards: DeliveryCard[] }
interface DeliveryProject { projectId: string; lanes: DeliveryLane[] }

@Component({
  selector: 'app-delivery-lanes',
  standalone: true,
  imports: [DatePipe],
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
  readonly rows = computed(() => this.data().map(project => ({
    ...project,
    lanes: project.lanes.map(lane => ({
      ...lane,
      cards: lane.cards.filter(card => {
        const keys = this.visibleKeys() ?? this.filteredKeys();
        return keys === null || keys.has(card.key.toLowerCase());
      }),
    })),
  })));

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

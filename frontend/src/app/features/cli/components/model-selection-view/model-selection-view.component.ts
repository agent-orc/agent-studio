import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { QuotaApiService } from '../../../quota';
import type { ActiveModelSwitch, EffectiveModelSelection, ModelSelectionEvent, SelectionRoute } from '../../../quota/services/quota-api.service';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';

export type SelectionPeriod = '24h' | '7d' | '30d' | 'custom';
export interface SelectionFilters {
  provider: string; project: string; consumer: string; rule: string;
  period: SelectionPeriod; start: string; end: string; taskKey: string; eventId: string;
}
const defaults: SelectionFilters = { provider: '', project: '', consumer: '', rule: '',
  period: '7d', start: '', end: '', taskKey: '', eventId: '' };
const queryNames: Record<keyof SelectionFilters, string> = {
  provider: 'msProvider', project: 'msProject', consumer: 'msConsumer', rule: 'msRule',
  period: 'msPeriod', start: 'msStart', end: 'msEnd', taskKey: 'msCard', eventId: 'msEvent',
};

export function readSelectionFilters(search: string): SelectionFilters {
  const params = new URLSearchParams(search);
  const period = params.get(queryNames.period);
  return {
    ...defaults,
    ...Object.fromEntries((Object.keys(queryNames) as (keyof SelectionFilters)[])
      .filter(key => key !== 'period').map(key => [key, params.get(queryNames[key]) ?? ''])),
    period: period === '24h' || period === '30d' || period === 'custom' ? period : '7d',
  };
}

export function selectionQuery(filters: SelectionFilters, now = new Date()): Record<string, string> {
  const query: Record<string, string> = {};
  for (const key of ['provider', 'project', 'consumer', 'rule', 'taskKey', 'eventId'] as const) {
    if (filters[key]) query[key] = filters[key];
  }
  // An event deep link stays usable after the normal period has elapsed.
  if (filters.eventId) return query;
  if (filters.period === 'custom') {
    if (filters.start) query['since'] = new Date(`${filters.start}T00:00:00`).toISOString();
    if (filters.end) query['until'] = new Date(`${filters.end}T23:59:59`).toISOString();
  } else {
    const days = filters.period === '24h' ? 1 : filters.period === '7d' ? 7 : 30;
    query['since'] = new Date(now.getTime() - days * 86400000).toISOString();
  }
  return query;
}

@Component({
  selector: 'app-model-selection-view', standalone: true,
  imports: [DisclosureMarkerComponent],
  templateUrl: './model-selection-view.component.html',
  styleUrl: './model-selection-view.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModelSelectionViewComponent implements OnInit {
  private readonly api = inject(QuotaApiService);
  readonly filters = signal<SelectionFilters>(readSelectionFilters(window.location.search));
  readonly active = signal<ActiveModelSwitch[]>([]);
  readonly events = signal<ModelSelectionEvent[]>([]);
  readonly effective = signal<EffectiveModelSelection[]>([]);
  readonly nextCursor = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly expanded = signal<string | null>(null);
  readonly saving = signal<string | null>(null);
  readonly providers = computed(() => [...new Set([this.filters().provider, ...this.active().map(item => item.from?.cliType),
    ...this.events().flatMap(item => [item.from?.cliType, item.to?.cliType])].filter((item): item is string => !!item))].sort());
  readonly projects = computed(() => [...new Set([this.filters().project, ...this.events().map(item => item.project)]
    .filter((item): item is string => !!item))].sort());
  readonly consumers = computed(() => [...new Set([this.filters().consumer,
    ...this.events().map(item => item.consumer), ...this.effective().map(item => item.consumer)].filter(Boolean))].sort());
  readonly rules = computed(() => [...new Set([this.filters().rule, ...this.events().map(item => item.rule)].filter(Boolean))].sort());
  readonly groupedActive = computed(() => {
    const map = new Map<string, ActiveModelSwitch[]>();
    for (const item of this.active()) {
      const provider = item.from?.cliType ?? 'Unknown provider';
      map.set(provider, [...(map.get(provider) ?? []), item]);
    }
    return [...map].map(([provider, items]) => ({ provider, items,
      cards: new Set(items.map(item => item.taskKey).filter(Boolean)).size }));
  });

  ngOnInit(): void { this.reload(); }

  setFilter(key: keyof SelectionFilters, value: string): void {
    this.filters.update(current => ({ ...current, [key]: value }));
    this.writeUrl();
    this.loadEvents();
  }

  reload(): void {
    this.api.getActiveModelSwitches().subscribe({ next: items => this.active.set(items),
      error: () => this.error.set('Could not load active fallbacks.') });
    this.api.getEffectiveModelSelection().subscribe({ next: data => this.effective.set(data.routes),
      error: () => this.error.set('Could not load effective routes.') });
    this.loadEvents();
  }

  loadEvents(cursor?: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getModelSelectionEvents({ ...selectionQuery(this.filters()), ...(cursor ? { cursor } : {}) }).subscribe({
      next: page => {
        this.events.set(cursor ? [...this.events(), ...page.events] : page.events);
        this.nextCursor.set(page.nextCursor);
        this.loading.set(false);
      },
      error: () => { this.error.set('Could not load model selection events.'); this.loading.set(false); },
    });
  }

  returnToPrimary(item: ActiveModelSwitch): void {
    if (item.rule !== 'operator-switch' || !item.from?.cliType) return;
    this.saving.set(item.id);
    this.api.setFallbackPreference(item.from.cliType, false).subscribe({
      next: () => { this.saving.set(null); this.reload(); },
      error: () => { this.saving.set(null); this.error.set('Could not end the operator switch.'); },
    });
  }

  toggle(id: string): void { this.expanded.set(this.expanded() === id ? null : id); }
  route(route: SelectionRoute | null): string {
    return route ? `${route.cliType ?? 'unknown'} / ${route.model ?? 'default'}` : 'Waiting';
  }
  cardHref(item: ModelSelectionEvent): string {
    return `/?job=${encodeURIComponent(item.taskKey ?? '')}`;
  }
  formatTime(value: string | null): string {
    return value ? new Date(value).toLocaleString('en-US') : 'Not known';
  }

  private writeUrl(): void {
    const url = new URL(window.location.href);
    for (const [key, name] of Object.entries(queryNames) as [keyof SelectionFilters, string][]) {
      const value = this.filters()[key];
      if (value && (key !== 'period' || value !== '7d')) url.searchParams.set(name, value);
      else url.searchParams.delete(name);
    }
    history.replaceState(history.state, '', url.pathname + url.search + url.hash);
  }
}

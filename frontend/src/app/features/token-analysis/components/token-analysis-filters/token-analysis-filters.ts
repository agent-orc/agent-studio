import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { laneName } from '../../../../models/lane-presentation';
import type { FilterOptions, TokenAnalysisFilters } from '../../token-analysis.util';
import { ANALYSIS_DAY_OPTIONS, ANALYSIS_ROUND_OPTIONS, EMPTY_FILTERS, kindLabel } from '../../token-analysis.util';

type DimensionKey = 'project' | 'lane' | 'type' | 'tag' | 'model' | 'level' | 'cli' | 'host' | 'kind' | 'outcome';

interface Dimension {
  key: DimensionKey;
  label: string;
  options: { value: string; label: string }[];
}

/** One filter row above the charts; every change is written to the URL by the host (AGT-3267). */
@Component({
  selector: 'app-token-analysis-filters',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-filters.html',
  styleUrl: './token-analysis-filters.scss',
})
export class TokenAnalysisFiltersComponent {
  readonly filters = input.required<TokenAnalysisFilters>();
  readonly options = input.required<FilterOptions>();
  /** Project scope: the project filter is fixed and hidden. */
  readonly fixedProject = input<string | null>(null);
  readonly filtersChange = output<TokenAnalysisFilters>();

  readonly dayOptions = ANALYSIS_DAY_OPTIONS;
  readonly roundOptions = ANALYSIS_ROUND_OPTIONS;

  readonly dimensions = computed<Dimension[]>(() => {
    const o = this.options();
    const plain = (values: string[]) => values.map(value => ({ value, label: value }));
    const all: Dimension[] = [
      { key: 'project', label: 'Project', options: plain(o.projects) },
      { key: 'lane', label: 'Lane', options: o.lanes.map(value => ({ value, label: laneName(value) })) },
      { key: 'type', label: 'Card type', options: plain(o.types) },
      { key: 'tag', label: 'Tag', options: plain(o.tags) },
      { key: 'model', label: 'Model', options: plain(o.models) },
      { key: 'level', label: 'Reasoning', options: plain(o.levels) },
      { key: 'cli', label: 'CLI', options: plain(o.clis) },
      { key: 'host', label: 'Host', options: plain(o.hosts) },
      { key: 'kind', label: 'Usage kind', options: o.kinds.map(value => ({ value, label: kindLabel(value) })) },
      { key: 'outcome', label: 'Outcome', options: plain(o.outcomes) },
    ];
    return all.filter(d => !(d.key === 'project' && this.fixedProject()));
  });

  readonly active = computed(() => {
    const f = this.filters();
    return this.dimensions().some(d => f[d.key] !== null) || f.minRounds !== null
      || f.days !== EMPTY_FILTERS.days;
  });

  value(key: DimensionKey): string {
    return this.filters()[key] ?? '';
  }

  /** A value the URL carries but the data no longer has stays selectable. */
  optionsFor(dimension: Dimension): { value: string; label: string }[] {
    const current = this.filters()[dimension.key];
    return current && !dimension.options.some(o => o.value === current)
      ? [...dimension.options, { value: current, label: current }]
      : dimension.options;
  }

  set(key: DimensionKey, event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.filtersChange.emit({ ...this.filters(), [key]: value || null });
  }

  setDays(event: Event): void {
    this.filtersChange.emit({ ...this.filters(), days: Number((event.target as HTMLSelectElement).value) });
  }

  setRounds(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);
    this.filtersChange.emit({ ...this.filters(), minRounds: value > 1 ? value : null });
  }

  reset(): void {
    this.filtersChange.emit({ ...EMPTY_FILTERS, card: null });
  }
}

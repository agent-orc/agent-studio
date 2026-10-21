import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { untracked } from '@angular/core';
import type { ReviewMetric, ReviewStatistics, ReviewUsageRow } from './review-statistics.model';

interface Tile { key: string; title: string; metric: ReviewMetric; unit: 'tokens' | 'usd' | 'number' | 'percent'; step?: string; modelLevel?: string }

/** Read-only review economics shared by the usage cockpit and project overview. */
@Component({
  selector: 'app-review-statistics',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './review-statistics.html',
  styleUrl: './review-statistics.scss',
})
export class ReviewStatisticsComponent {
  private readonly http = inject(HttpClient);
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  readonly projectName = input<string | null>(null);
  readonly projects = input<readonly string[]>([]);
  readonly selectedProject = signal('');
  readonly project = computed(() => this.selectedProject() || this.projectName() || this.projects()[0] || '');
  readonly days = signal(30);
  readonly model = signal('');
  readonly level = signal('');
  readonly host = signal('');
  readonly data = signal<ReviewStatistics | null>(null);
  readonly loading = signal(false);
  readonly failed = signal(false);
  readonly listStep = signal('');
  readonly listModelLevel = signal('');
  readonly acceptedOnly = signal(false);
  readonly visibleReviews = computed(() => this.data()?.reviews.filter(row =>
    (!this.listStep() || row.step === this.listStep()) &&
    (!this.listModelLevel() || `${row.model} / ${row.level}` === this.listModelLevel()) &&
    (!this.acceptedOnly() || this.data()?.acceptedCards.includes(`${row.project}/${row.jobId}`))) ?? []);
  readonly tiles = computed<Tile[]>(() => {
    const data = this.data();
    if (!data) return [];
    return [
      { key: 'delivery-tokens', title: 'Tokens per delivery review', metric: data.deliveryTokens, unit: 'tokens' },
      { key: 'delivery-usd', title: 'USD per delivery review', metric: data.deliveryUsd, unit: 'usd' },
      { key: 'rounds', title: 'Rounds until acceptance', metric: data.roundsUntilAcceptance, unit: 'number' },
      { key: 'accepted-tokens', title: 'Review tokens per accepted card', metric: data.tokensPerAcceptedCard, unit: 'tokens' },
      { key: 'accepted-usd', title: 'Review USD per accepted card', metric: data.usdPerAcceptedCard, unit: 'usd' },
      { key: 'share', title: 'Review share of card tokens', metric: data.reviewSharePercent, unit: 'percent' },
      ...Object.entries(data.byStepTokens).map(([step, metric]) => ({ key: `step-${step}`, title: `${step} tokens`, metric, unit: 'tokens' as const, step })),
      ...Object.entries(data.byStepUsd).map(([step, metric]) => ({ key: `step-usd-${step}`, title: `${step} USD`, metric, unit: 'usd' as const, step })),
      ...Object.entries(data.byModelLevelTokens).map(([modelLevel, metric]) => ({ key: `model-${modelLevel}`, title: `${modelLevel} tokens`, metric, unit: 'tokens' as const, modelLevel })),
      ...Object.entries(data.byModelLevelUsd).map(([modelLevel, metric]) => ({ key: `model-usd-${modelLevel}`, title: `${modelLevel} USD`, metric, unit: 'usd' as const, modelLevel })),
    ];
  });
  readonly models = computed(() => [...new Set(this.data()?.reviews.map(row => row.model) ?? [])].sort());
  readonly levels = computed(() => [...new Set(this.data()?.reviews.map(row => row.level) ?? [])].sort());
  readonly hosts = computed(() => [...new Set(this.data()?.reviews.map(row => row.host) ?? [])].sort());

  constructor() {
    effect(onCleanup => {
      const project = this.project();
      const days = this.days();
      const model = this.model();
      const level = this.level();
      const host = this.host();
      if (!project) return;
      untracked(() => { this.loading.set(true); this.failed.set(false); });
      let params = new HttpParams().set('days', days);
      if (model) params = params.set('model', model);
      if (level) params = params.set('level', level);
      if (host) params = params.set('host', host);
      const request = this.http.get<ReviewStatistics>(`/api/projects/${encodeURIComponent(project)}/token-usage/reviews`, { params })
        .subscribe({
          next: data => { this.data.set(data); this.loading.set(false); },
          error: () => { this.data.set(null); this.loading.set(false); this.failed.set(true); },
        });
      onCleanup(() => request.unsubscribe());
    });
  }

  selectProject(value: string): void { this.selectedProject.set(value); this.clearListFilter(); }
  setDays(value: string): void { this.days.set(Number(value)); this.clearListFilter(); }
  selectModel(value: string): void { this.model.set(value); this.clearListFilter(); }
  selectLevel(value: string): void { this.level.set(value); this.clearListFilter(); }
  selectHost(value: string): void { this.host.set(value); this.clearListFilter(); }
  clearListFilter(): void { this.listStep.set(''); this.listModelLevel.set(''); this.acceptedOnly.set(false); }
  showReviews(tile: Tile): void {
    this.listStep.set(tile.step ?? '');
    this.listModelLevel.set(tile.modelLevel ?? '');
    this.acceptedOnly.set(['rounds', 'accepted-tokens', 'accepted-usd', 'share'].includes(tile.key));
    const details = this.element.nativeElement.querySelector('.review-stats__evidence') as HTMLDetailsElement | null;
    if (details) {
      details.open = true;
      details.scrollIntoView?.({ block: 'nearest' });
    }
  }
  format(value: number | null, unit: Tile['unit']): string {
    if (value == null) return 'Unknown';
    if (unit === 'usd') return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 3 }).format(value);
    if (unit === 'percent') return `${value.toFixed(1)}%`;
    return new Intl.NumberFormat('en-US', { maximumFractionDigits: unit === 'number' ? 1 : 0 }).format(value);
  }
  trend(values: (number | null)[]): string {
    const known = values.filter((v): v is number => v != null);
    if (!known.length) return 'No trend data';
    const max = Math.max(...known, 1);
    return values.map(v => v == null ? '·' : '▁▂▃▄▅▆▇█'[Math.min(7, Math.floor(v / max * 7))]).join('');
  }
  reviewKey(row: ReviewUsageRow): string { return `${row.project}:${row.jobId}:${row.at}:${row.step}`; }
}

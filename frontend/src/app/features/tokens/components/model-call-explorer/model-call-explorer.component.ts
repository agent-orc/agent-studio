import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';

interface CallTotals {
  calls: number;
  tokens: number;
  waits: number;
  statusChecks: number;
  freshInput: number;
  cachedInput: number;
  unclassifiedInput: number;
  output: number;
  pricedCalls: number;
  unpricedTokens: number;
  missingUsage: number;
  listUsd: number | null;
}

interface CallGroup {
  key: string;
  totals: CallTotals;
}

interface CallRow {
  id: string;
  ordinal: number;
  happenedAtMs: number;
  kind: string;
  tokens: number | null;
  knownTokens: number | null;
  priceStatus: string;
}

interface CallWindow {
  totals: CallTotals;
  groups: CallGroup[];
  other: CallTotals | null;
  responses: CallRow[];
  nextCursor: string | null;
}

interface CallDetail {
  ordinal: number;
  happenedAtMs: number;
  timestampBasis: string;
  usageBasis: string;
  linkBasis: string;
  kind: string;
  classifier: string | null;
  model: string | null;
  reasoningLevel: string | null;
  serviceTier: string | null;
  promptTokens: number | null;
  freshInput: number | null;
  cachedInput: number | null;
  output: number | null;
  reasoning: number | null;
  priceStatus: string;
  listUsd: number | null;
  price: {
    catalogRevision: string;
    dateBasis: string;
    validFrom: string;
    source: string | null;
    inputPerMillion: number;
    cacheReadPerMillion: number;
    outputPerMillion: number;
  } | null;
  usageRevision: number;
  sourceCardUrl: string | null;
}

interface LedgerProject {
  id: string;
  label: string;
}

@Component({
  selector: 'app-model-call-explorer',
  imports: [DatePipe, DecimalPipe, DisclosureMarkerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './model-call-explorer.component.html',
  styleUrl: './model-call-explorer.component.scss',
})
export class ModelCallExplorerComponent {
  private readonly http = inject(HttpClient);
  private generation = 0;
  private scope: { project: string; fromMs: number; untilMs: number; card?: string } | null = null;

  readonly projects = signal<LedgerProject[]>([]);
  readonly project = signal('');
  readonly from = signal(this.utcInput(Date.now() - 86_400_000));
  readonly until = signal(this.utcInput(Date.now() + 1000));
  readonly cardFilter = signal('');
  readonly appliedCard = signal('');
  readonly visibleCards = computed(() => {
    const window = this.result();
    if (!window) return [];
    return this.appliedCard()
      ? [{ key: this.appliedCard(), totals: window.totals }]
      : window.groups;
  });
  readonly result = signal<CallWindow | null>(null);
  readonly card = signal<string | null>(null);
  readonly runs = signal<CallWindow | null>(null);
  readonly run = signal<string | null>(null);
  readonly responses = signal<CallWindow | null>(null);
  readonly detail = signal<CallDetail | null>(null);
  readonly error = signal('');
  readonly loading = signal(false);

  constructor() {
    this.http.get<{ projectId?: string; name?: string; id?: string; displayName?: string }[]>('/api/v1/projects')
      .pipe(takeUntilDestroyed())
      .subscribe({
        next: projects => {
          const available = projects.flatMap(item => {
            const id = item.projectId ?? item.id;
            const label = item.name ?? item.displayName;
            return id && label ? [{ id, label }] : [];
          });
          this.projects.set(available);
          if (available.length === 1) this.project.set(available[0].id);
        },
        error: () => this.error.set('Projects could not be loaded.'),
      });
  }

  async apply(): Promise<void> {
    const fromMs = Date.parse(this.from() + 'Z');
    const untilMs = Date.parse(this.until() + 'Z');
    const card = this.cardFilter().trim().toUpperCase();
    this.clearSelection();
    this.result.set(null);
    this.appliedCard.set(card);
    this.scope = null;
    if (!this.project() || !Number.isFinite(fromMs) || !Number.isFinite(untilMs) || fromMs >= untilMs ||
        untilMs - fromMs > 31 * 86_400_000) {
      this.error.set('Choose a project and a UTC window of up to 31 days.');
      return;
    }
    this.scope = { project: this.project(), fromMs, untilMs, card: card || undefined };
    await this.load(() => this.window({}), result => this.result.set(result));
  }

  async openCard(card: string): Promise<void> {
    this.clearSelection();
    this.card.set(card);
    if (card === this.appliedCard()) {
      this.runs.set(this.result());
      return;
    }
    await this.load(() => this.window({ card }), result => this.runs.set(result));
  }

  async openRun(run: string): Promise<void> {
    this.run.set(run);
    this.responses.set(null);
    this.detail.set(null);
    await this.load(() => this.window({ card: this.card()!, run }), result => this.responses.set(result));
  }

  async more(): Promise<void> {
    const previous = this.responses();
    if (!previous?.nextCursor || !this.card() || !this.run()) return;
    const params = { card: this.card()!, run: this.run()!, cursor: previous.nextCursor };
    await this.load(() => this.window(params), result => this.responses.set({
      ...result,
      responses: [...previous.responses, ...result.responses],
    }));
  }

  async openResponse(id: string): Promise<void> {
    this.detail.set(null);
    await this.load(() => firstValueFrom(this.http.get<CallDetail>(this.url(id))), detail => this.detail.set(detail));
  }

  clearSelection(): void {
    this.generation++;
    this.loading.set(false);
    this.error.set('');
    this.card.set(null);
    this.runs.set(null);
    this.run.set(null);
    this.responses.set(null);
    this.detail.set(null);
  }

  label(value: string): string {
    switch (value) {
      case 'wait': return 'Wait';
      case 'status': return 'Status check';
      case 'work': return 'Work';
      default: return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/-/g, ' ');
    }
  }

  private window(filters: Record<string, string>): Promise<CallWindow> {
    const scope = this.scope!;
    const params = {
      fromMs: scope.fromMs,
      untilMs: scope.untilMs,
      ...(scope.card ? { card: scope.card } : {}),
      ...filters,
    };
    return firstValueFrom(this.http.get<CallWindow>(this.url('query'), { params }));
  }

  private url(segment: string): string {
    return `/api/v1/projects/${encodeURIComponent(this.scope!.project)}/model-calls/${encodeURIComponent(segment)}`;
  }

  private utcInput(ms: number): string {
    return new Date(ms).toISOString().slice(0, 23);
  }

  private async load<T>(request: () => Promise<T>, accept: (value: T) => void): Promise<void> {
    const generation = ++this.generation;
    this.loading.set(true);
    this.error.set('');
    try {
      const value = await request();
      if (generation === this.generation) accept(value);
    } catch {
      if (generation === this.generation) this.error.set('Model Calls could not load for this project and window.');
    } finally {
      if (generation === this.generation) this.loading.set(false);
    }
  }
}

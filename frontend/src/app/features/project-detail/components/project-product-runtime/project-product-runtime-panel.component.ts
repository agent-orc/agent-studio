import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ProductRuntimeFixture,
  ProductRuntimeService,
} from '../../../../services/product-runtime.service';
import {
  PRODUCT_RUNTIME_LEVELS,
  ProductRuntimeEvent,
  ProductRuntimeLevel,
  RuntimeEventParseWarning,
} from '../../../../models/product-runtime.model';

interface FilterState {
  level: ProductRuntimeLevel | '';
  event: string;
  subsystem: string;
  jobId: string;
  runId: string;
  correlationId: string;
  rangeHours: number;
}

const DEFAULT_FILTER: FilterState = {
  level: '',
  event: '',
  subsystem: '',
  jobId: '',
  runId: '',
  correlationId: '',
  rangeHours: 24,
};

const RANGE_OPTIONS: readonly { id: number; label: string }[] = [
  { id: 1, label: 'Last 1h' },
  { id: 6, label: 'Last 6h' },
  { id: 24, label: 'Last 24h' },
  { id: 24 * 7, label: 'Last 7d' },
  { id: 0, label: 'All time' },
];

const LEVEL_RANK: Record<ProductRuntimeLevel, number> = {
  Trace: 0, Debug: 1, Info: 2, Warn: 3, Error: 4, Fatal: 5,
};

interface CountChip {
  testid: string;
  label: string;
  value: number | string;
  tone: 'neutral' | 'warn' | 'high';
}

interface ErrorGroup {
  key: string;
  event: string;
  errorType: string;
  count: number;
  lastSeen: string;
  retryable: boolean | null;
  sampleMessage: string;
  jobId: string | null;
  runId: string | null;
}

interface LatencyRow {
  key: string;
  subsystem: string;
  operation: string;
  count: number;
  p50: number;
  p95: number;
  max: number;
}

interface CounterRow {
  key: string;
  subsystem: string;
  event: string;
  total: number;
  warns: number;
  errors: number;
}

interface DomainTimelineEntry {
  id: string;
  timestamp: string;
  event: string;
  subsystem: string;
  level: ProductRuntimeLevel;
  status: string | null;
  correlationId: string | null;
  durationMs: number | null;
  groupKey: string;
}

const DOMAIN_TIMELINE_ALLOW_PREFIXES = ['order.', 'payment.', 'render.', 'job.', 'auth.', 'task.', 'http.request.'];

/**
 * Project Product Runtime Observability panel: renders the structured runtime
 * stream emitted by the software the agents are building, distinct from the
 * Agent Message Bus surface. Reads {@code /api/runtime/{project}/events} via
 * {@link ProductRuntimeService}. When the live stream is empty, the panel
 * offers a fixture toggle ({@link ProductRuntimeFixture}) so reviewers can
 * exercise every surface and Playwright can capture screenshots.
 *
 * Surfaces, top-down:
 *  - Counter chips: total events, errors, warnings, p95 latency, top
 *    subsystem, correlation count, malformed-line count.
 *  - Recent events feed (newest first, 50 rows) with drill-down to JSON.
 *  - Error groups: events with level Error/Fatal grouped by event +
 *    error.type, with last-seen + retryable hint.
 *  - Latency summary: p50 / p95 / max per (subsystem, operation) over
 *    Ok-status events.
 *  - Counters per (subsystem, event) with warn/error split.
 *  - Domain timeline: filtered to product-shaped events (order.*, payment.*,
 *    render.*, job.*, http.request.*) ordered chronologically.
 *  - Malformed-line warnings, kept compact and only when present.
 *
 * Read-only by contract: nothing on this panel mutates state, runs an
 * agent, or moves a job; runtime events are pure output.
 */
@Component({
  selector: 'app-project-product-runtime-panel',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-product-runtime-panel.component.html',
  styleUrl: './project-product-runtime-panel.component.scss',
})
export class ProjectProductRuntimePanelComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly runtime = inject(ProductRuntimeService);

  readonly Math = Math;
  readonly levels: readonly ProductRuntimeLevel[] = PRODUCT_RUNTIME_LEVELS;
  readonly rangeOptions = RANGE_OPTIONS;

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly events = signal<ProductRuntimeEvent[]>([]);
  readonly warnings = signal<RuntimeEventParseWarning[]>([]);
  readonly usingFixture = signal<boolean>(false);
  readonly selectedKey = signal<string | null>(null);

  filter: FilterState = { ...DEFAULT_FILTER };
  readonly filterTick = signal(0);

  private pollTimer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (!name) return;
      this.refresh();
    });
  }

  ngOnInit(): void {
    this.pollTimer = setInterval(() => this.refresh(true), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(silent = false): void {
    const name = this.projectName();
    if (!name) return;
    if (!silent) this.loading.set(true);
    this.loadError.set(null);
    this.runtime.getEvents(name).subscribe({
      next: (resp) => {
        if (this.usingFixture()) {
          this.loading.set(false);
          return;
        }
        this.events.set(resp.events ?? []);
        this.warnings.set(resp.warnings ?? []);
        this.loading.set(false);
      },
      error: (err) => {
        this.loading.set(false);
        this.loadError.set(err?.message ?? 'request failed');
      },
    });
  }

  loadFixture(): void {
    const name = this.projectName();
    const sample = ProductRuntimeFixture.sample(name);
    this.events.set(sample.events);
    this.warnings.set(sample.warnings);
    this.usingFixture.set(true);
    this.loading.set(false);
    this.loadError.set(null);
  }

  resetFilters(): void {
    this.filter = { ...DEFAULT_FILTER };
    this.filterTick.update(v => v + 1);
    this.selectedKey.set(null);
  }

  onFilterChanged(): void {
    this.filterTick.update(v => v + 1);
  }

  onRangeChanged(value: string | number): void {
    const n = typeof value === 'number' ? value : Number(value);
    this.filter.rangeHours = Number.isFinite(n) ? n : 0;
    this.filterTick.update(v => v + 1);
  }

  select(e: ProductRuntimeEvent): void {
    this.selectedKey.set(this.rowKey(e));
  }
  clearSelection(): void { this.selectedKey.set(null); }

  rowKey(e: ProductRuntimeEvent): string {
    return `${e.timestamp}|${e.event}|${e.subsystem}|${e.operation ?? ''}|${e.correlationId ?? ''}`;
  }

  readonly sourceLabel = computed(() => {
    if (this.usingFixture()) return 'Fixture';
    const total = this.events().length;
    return total > 0 ? 'Live' : 'Empty';
  });

  readonly rangeStart = computed(() => {
    void this.filterTick();
    if (this.filter.rangeHours <= 0) {
      const evs = this.events();
      if (!evs.length) return new Date().toISOString();
      let min = evs[0].timestamp;
      for (const e of evs) if (e.timestamp < min) min = e.timestamp;
      return min;
    }
    return new Date(Date.now() - this.filter.rangeHours * 3600 * 1000).toISOString();
  });
  readonly rangeEnd = computed(() => new Date().toISOString());

  readonly filtered = computed<ProductRuntimeEvent[]>(() => {
    void this.filterTick();
    const f = this.filter;
    const start = this.rangeStart();
    const end = this.rangeEnd();
    const minRank = f.level ? LEVEL_RANK[f.level] : -1;
    return this.events()
      .filter(e => {
        if (e.timestamp < start || e.timestamp > end) return false;
        if (minRank >= 0 && LEVEL_RANK[e.level] < minRank) return false;
        if (f.event && e.event !== f.event) return false;
        if (f.subsystem && e.subsystem !== f.subsystem) return false;
        if (f.jobId && e.jobId !== f.jobId) return false;
        if (f.runId && e.runId !== f.runId.trim()) return false;
        if (f.correlationId && e.correlationId !== f.correlationId.trim()) return false;
        return true;
      })
      .sort((a, b) => (a.timestamp < b.timestamp ? 1 : -1));
  });

  readonly filteredHead = computed(() => this.filtered().slice(0, 50));

  readonly eventOptions = computed(() => {
    const set = new Set<string>();
    for (const e of this.events()) set.add(e.event);
    return Array.from(set).sort();
  });
  readonly subsystemOptions = computed(() => {
    const set = new Set<string>();
    for (const e of this.events()) set.add(e.subsystem);
    return Array.from(set).sort();
  });
  readonly jobOptions = computed(() => {
    const set = new Set<string>();
    for (const e of this.events()) if (e.jobId) set.add(e.jobId);
    return Array.from(set).sort();
  });

  readonly countChips = computed<CountChip[]>(() => {
    const evs = this.filtered();
    let errors = 0, warns = 0;
    const subBuckets = new Map<string, number>();
    const corrSet = new Set<string>();
    const okMs: number[] = [];
    for (const e of evs) {
      if (e.level === 'Error' || e.level === 'Fatal') errors++;
      else if (e.level === 'Warn') warns++;
      subBuckets.set(e.subsystem, (subBuckets.get(e.subsystem) ?? 0) + 1);
      if (e.correlationId) corrSet.add(e.correlationId);
      if (e.status === 'Ok' && e.duration && Number.isFinite(e.duration.ms)) {
        okMs.push(e.duration.ms);
      }
    }
    let topSub = '–';
    let topCount = 0;
    for (const [k, v] of subBuckets) {
      if (v > topCount) { topSub = k; topCount = v; }
    }
    const p95 = percentile(okMs, 0.95);
    const p95Label = p95 === null ? '–' : formatMsStatic(p95);
    const malformed = this.warnings().length;
    return [
      { testid: 'product-runtime-counter-total', label: 'Events', value: evs.length, tone: 'neutral' },
      { testid: 'product-runtime-counter-errors', label: 'Errors', value: errors, tone: errors > 0 ? 'high' : 'neutral' },
      { testid: 'product-runtime-counter-warns', label: 'Warnings', value: warns, tone: warns > 0 ? 'warn' : 'neutral' },
      { testid: 'product-runtime-counter-p95', label: 'p95 latency', value: p95Label, tone: 'neutral' },
      { testid: 'product-runtime-counter-top-sub', label: 'Top subsystem', value: topCount > 0 ? `${topSub} · ${topCount}` : '–', tone: 'neutral' },
      { testid: 'product-runtime-counter-correlations', label: 'Correlations', value: corrSet.size, tone: 'neutral' },
      { testid: 'product-runtime-counter-malformed', label: 'Malformed lines', value: malformed, tone: malformed > 0 ? 'warn' : 'neutral' },
    ];
  });

  readonly errorGroups = computed<ErrorGroup[]>(() => {
    const map = new Map<string, ErrorGroup>();
    for (const e of this.filtered()) {
      if (e.level !== 'Error' && e.level !== 'Fatal') continue;
      const errType = e.error?.type ?? '';
      const key = `${e.event}|${errType}`;
      const existing = map.get(key);
      if (existing) {
        existing.count++;
        if (e.timestamp > existing.lastSeen) existing.lastSeen = e.timestamp;
      } else {
        map.set(key, {
          key,
          event: e.event,
          errorType: errType,
          count: 1,
          lastSeen: e.timestamp,
          retryable: e.error?.retryable ?? null,
          sampleMessage: e.error?.message ?? '',
          jobId: e.jobId ?? null,
          runId: e.runId ?? null,
        });
      }
    }
    return Array.from(map.values()).sort((a, b) => b.count - a.count || (a.lastSeen < b.lastSeen ? 1 : -1));
  });

  readonly latencyRows = computed<LatencyRow[]>(() => {
    const map = new Map<string, { sub: string; op: string; samples: number[] }>();
    for (const e of this.filtered()) {
      if (e.status !== 'Ok' || !e.duration || !Number.isFinite(e.duration.ms)) continue;
      const op = e.operation ?? '(none)';
      const key = `${e.subsystem}|${op}`;
      let bucket = map.get(key);
      if (!bucket) { bucket = { sub: e.subsystem, op, samples: [] }; map.set(key, bucket); }
      bucket.samples.push(e.duration.ms);
    }
    const rows: LatencyRow[] = [];
    for (const [key, b] of map) {
      const sorted = [...b.samples].sort((a, c) => a - c);
      rows.push({
        key,
        subsystem: b.sub,
        operation: b.op,
        count: sorted.length,
        p50: percentile(sorted, 0.50) ?? 0,
        p95: percentile(sorted, 0.95) ?? 0,
        max: sorted[sorted.length - 1] ?? 0,
      });
    }
    return rows.sort((a, b) => b.p95 - a.p95);
  });

  readonly counterRows = computed<CounterRow[]>(() => {
    const map = new Map<string, CounterRow>();
    for (const e of this.filtered()) {
      const key = `${e.subsystem}|${e.event}`;
      const existing = map.get(key);
      if (existing) {
        existing.total++;
        if (e.level === 'Warn') existing.warns++;
        if (e.level === 'Error' || e.level === 'Fatal') existing.errors++;
      } else {
        map.set(key, {
          key,
          subsystem: e.subsystem,
          event: e.event,
          total: 1,
          warns: e.level === 'Warn' ? 1 : 0,
          errors: e.level === 'Error' || e.level === 'Fatal' ? 1 : 0,
        });
      }
    }
    return Array.from(map.values()).sort((a, b) => b.total - a.total);
  });

  readonly domainTimeline = computed<DomainTimelineEntry[]>(() => {
    const out: DomainTimelineEntry[] = [];
    for (const e of this.filtered()) {
      if (!DOMAIN_TIMELINE_ALLOW_PREFIXES.some(p => e.event.startsWith(p))) continue;
      out.push({
        id: this.rowKey(e),
        timestamp: e.timestamp,
        event: e.event,
        subsystem: e.subsystem,
        level: e.level,
        status: e.status ?? null,
        correlationId: e.correlationId ?? null,
        durationMs: e.duration?.ms ?? null,
        groupKey: e.correlationId ?? e.event,
      });
    }
    // Sort by correlation group for readability, then by time within group.
    return out.sort((a, b) => {
      if (a.groupKey !== b.groupKey) return a.groupKey < b.groupKey ? -1 : 1;
      return a.timestamp < b.timestamp ? -1 : 1;
    });
  });

  readonly selectedEvent = computed<ProductRuntimeEvent | null>(() => {
    const key = this.selectedKey();
    if (!key) return null;
    return this.events().find(e => this.rowKey(e) === key) ?? null;
  });

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString('en-US');
    } catch { return iso; }
  }
  formatMs(ms: number): string { return formatMsStatic(ms); }
  formatJson(e: ProductRuntimeEvent): string { try { return JSON.stringify(e, null, 2); } catch { return String(e); } }
  isDomainProblem(e: DomainTimelineEntry): boolean { return e.level === 'Error' || e.level === 'Fatal' || e.status === 'Failed' || e.status === 'Timeout'; }
}

function percentile(sorted: number[], p: number): number | null {
  if (sorted.length === 0) return null;
  const a = [...sorted].sort((x, y) => x - y);
  const idx = Math.min(a.length - 1, Math.max(0, Math.floor(p * (a.length - 1))));
  return a[idx];
}

function formatMsStatic(ms: number): string {
  if (!Number.isFinite(ms)) return '–';
  if (ms < 1) return ms.toFixed(2) + 'ms';
  if (ms < 1000) return Math.round(ms) + 'ms';
  return (ms / 1000).toFixed(2) + 's';
}

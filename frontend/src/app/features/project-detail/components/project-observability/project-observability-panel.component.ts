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
import { AgentBusService, AgentBusFixture } from '../../../../services/agent-bus.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  AGENT_MESSAGE_KINDS,
  AGENT_MESSAGE_SEVERITIES,
  AgentMessage,
  AgentMessageSummary,
} from '../../../../models/agent-bus.model';

interface FilterState {
  participantId: string;
  kind: string;
  severity: string;
  jobId: string;
  runId: string;
  cli: string;
  skill: string;
  rangeHours: number;
}

interface CountChip {
  testid: string;
  label: string;
  value: number;
  tone: 'neutral' | 'warn' | 'high';
}

interface OutcomeIssueChip {
  testid: string;
  topic: string;
  label: string;
  value: number;
  tone: 'warn' | 'high';
  latestMessageId: string;
}

interface MatrixRow {
  participantId: string;
  total: number;
  kindCounts: Partial<Record<string, number>>;
  jobs: ReadonlySet<string>;
}

interface TimelineLane {
  participantId: string;
  marks: readonly { id: string; offsetPct: number; kind: string; severity: string | null; title: string }[];
}

interface HeatmapCell {
  participantId: string;
  bucket: number;
  tokens: number;
  msgIds: string[];
}

const HEATMAP_BUCKETS = 12;
const SILENT_GAP_THRESHOLD_MS = 1000 * 60 * 30; // a 30-minute gap counts as a silent period

const DEFAULT_FILTER: FilterState = {
  participantId: '',
  kind: '',
  severity: '',
  jobId: '',
  runId: '',
  cli: '',
  skill: '',
  rangeHours: 24,
};

const RANGE_OPTIONS: readonly { id: number; label: string }[] = [
  { id: 1, label: 'Last 1h' },
  { id: 6, label: 'Last 6h' },
  { id: 24, label: 'Last 24h' },
  { id: 24 * 7, label: 'Last 7d' },
  { id: 0, label: 'All time' },
];

const OUTCOME_ISSUE_TOPICS: readonly { topic: string; label: string; tone: 'warn' | 'high' }[] = [
  { topic: 'permission-blocked', label: 'Permission blocked', tone: 'high' },
  { topic: 'watchdog-timeout', label: 'Watchdog timeout', tone: 'high' },
  { topic: 'missing-terminal-sentinel', label: 'Missing sentinel', tone: 'warn' },
  { topic: 'classifier-unknown', label: 'Classifier unknown', tone: 'warn' },
  { topic: 'heuristic-done', label: 'Heuristic done', tone: 'warn' },
  { topic: 'soft-intervention', label: 'Soft intervention', tone: 'warn' },
];

/**
 * Project Observability panel: renders the Agent Message Bus as a dense,
 * scannable operations surface. Reads `/api/bus/{project}/...` via
 * {@link AgentBusService}; when the backend has not yet projected any
 * messages for a project, a fixture dataset (toggled per-panel) keeps the
 * surfaces useful in development and Playwright runs.
 *
 * Surfaces shipped here, all addressable through the filter strip at the
 * top:
 *  - Counter chips for total, intervention, error, severity-Warn,
 *    severity-High, token messages and silent-period count.
 *  - A per-participant timeline (rows = participants, columns = wall
 *    clock) with kind/severity glyphs.
 *  - A participant × kind matrix with reference counts.
 *  - A token-usage heatmap (participant × time bucket).
 *  - The filtered message table with raw-JSON drilldown.
 */
@Component({
  selector: 'app-project-observability-panel',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-observability-panel.component.html',
  styleUrl: './project-observability-panel.component.scss',
})
export class ProjectObservabilityPanelComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly bus = inject(AgentBusService);

  readonly kinds = AGENT_MESSAGE_KINDS;
  readonly severities = AGENT_MESSAGE_SEVERITIES;
  readonly rangeOptions = RANGE_OPTIONS;

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly summary = signal<AgentMessageSummary | null>(null);
  readonly messages = signal<AgentMessage[]>([]);
  readonly usingFixture = signal<boolean>(false);
  readonly selectedMessageId = signal<string | null>(null);

  filter: FilterState = { ...DEFAULT_FILTER };
  // ngModel needs a stable container; bumping a tick signal lets computeds rerun.
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
    this.bus.getSummary(name).subscribe({
      next: (s) => {
        this.summary.set(s);
      },
      error: () => this.summary.set(null),
    });
    this.bus.getRecent(name, 500).subscribe({
      next: (msgs) => {
        if (this.usingFixture()) {
          // Don't clobber a user-loaded fixture with an empty live response.
          this.loading.set(false);
          return;
        }
        this.messages.set(msgs);
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
    const sample = AgentBusFixture.sample(name);
    this.messages.set(sample);
    this.summary.set(AgentBusFixture.summary(name, sample));
    this.usingFixture.set(true);
    this.loading.set(false);
    this.loadError.set(null);
  }

  selectMessage(id: string): void {
    this.selectedMessageId.set(id);
  }
  selectFirst(ids: readonly string[]): void {
    if (ids.length > 0) this.selectedMessageId.set(ids[0]);
  }
  clearSelection(): void {
    this.selectedMessageId.set(null);
  }

  resetFilters(): void {
    this.filter = { ...DEFAULT_FILTER };
    this.filterTick.update(v => v + 1);
    this.selectedMessageId.set(null);
  }

  onFilterChanged(): void {
    this.filterTick.update(v => v + 1);
  }

  onRangeChanged(value: string | number): void {
    const n = typeof value === 'number' ? value : Number(value);
    this.filter.rangeHours = Number.isFinite(n) ? n : 0;
    this.filterTick.update(v => v + 1);
  }

  readonly sourceLabel = computed(() => {
    if (this.usingFixture()) return 'Fixture';
    const total = this.summary()?.totalMessages ?? this.messages().length;
    return total > 0 ? 'Live' : 'Empty';
  });

  readonly rangeStart = computed(() => {
    void this.filterTick();
    if (this.filter.rangeHours <= 0) {
      const msgs = this.messages();
      if (!msgs.length) return new Date().toISOString();
      let min = msgs[0].createdAt;
      for (const m of msgs) if (m.createdAt < min) min = m.createdAt;
      return min;
    }
    return new Date(Date.now() - this.filter.rangeHours * 3600 * 1000).toISOString();
  });
  readonly rangeEnd = computed(() => new Date().toISOString());

  readonly rangeLabel = computed(() => {
    void this.filterTick();
    return RANGE_OPTIONS.find(o => o.id === this.filter.rangeHours)?.label ?? 'Custom';
  });

  readonly filtered = computed<AgentMessage[]>(() => {
    void this.filterTick();
    const f = this.filter;
    const start = this.rangeStart();
    const end = this.rangeEnd();
    return this.messages().filter(m => {
      if (m.createdAt < start || m.createdAt > end) return false;
      if (f.participantId && m.participantId !== f.participantId) return false;
      if (f.kind && m.kind !== f.kind) return false;
      if (f.severity && m.severity !== f.severity) return false;
      if (f.jobId && m.jobId !== f.jobId) return false;
      if (f.runId && m.runId !== f.runId.trim()) return false;
      if (f.cli && !(m.tags ?? []).includes(`cli:${f.cli}`)) return false;
      if (f.skill && !(m.tags ?? []).includes(`skill:${f.skill}`)) return false;
      return true;
    }).sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1));
  });

  readonly participantIds = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) set.add(m.participantId);
    return Array.from(set).sort();
  });

  readonly jobIds = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) if (m.jobId) set.add(m.jobId);
    return Array.from(set).sort();
  });

  readonly cliOptions = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) {
      for (const t of m.tags ?? []) if (t.startsWith('cli:')) set.add(t.slice(4));
    }
    return Array.from(set).sort();
  });
  readonly skillOptions = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) {
      for (const t of m.tags ?? []) if (t.startsWith('skill:')) set.add(t.slice(6));
    }
    return Array.from(set).sort();
  });

  readonly countChips = computed<CountChip[]>(() => {
    const msgs = this.filtered();
    let interventions = 0, errors = 0, warns = 0, highs = 0, tokens = 0;
    for (const m of msgs) {
      if (m.kind === 'intervention') interventions++;
      if (m.kind === 'error') errors++;
      if (m.severity === 'Warn') warns++;
      if (m.severity === 'High') highs++;
      if (m.tokens) tokens += (m.tokens.input ?? 0) + (m.tokens.output ?? 0);
    }
    const sorted = [...msgs].sort((a, b) => (a.createdAt < b.createdAt ? -1 : 1));
    let silent = 0;
    for (let i = 1; i < sorted.length; i++) {
      const dt = new Date(sorted[i].createdAt).getTime() - new Date(sorted[i - 1].createdAt).getTime();
      if (dt >= SILENT_GAP_THRESHOLD_MS) silent++;
    }
    return [
      { testid: 'observability-counter-total', label: 'Messages', value: msgs.length, tone: 'neutral' },
      { testid: 'observability-counter-interventions', label: 'Interventions', value: interventions, tone: interventions > 0 ? 'high' : 'neutral' },
      { testid: 'observability-counter-errors', label: 'Errors', value: errors, tone: errors > 0 ? 'high' : 'neutral' },
      { testid: 'observability-counter-warn', label: 'Severity Warn', value: warns, tone: warns > 0 ? 'warn' : 'neutral' },
      { testid: 'observability-counter-high', label: 'Severity High', value: highs, tone: highs > 0 ? 'high' : 'neutral' },
      { testid: 'observability-counter-tokens', label: 'Tokens (sum)', value: tokens, tone: 'neutral' },
      { testid: 'observability-counter-silent', label: `Silent gaps (>${Math.round(SILENT_GAP_THRESHOLD_MS / 60000)}m)`, value: silent, tone: silent > 0 ? 'warn' : 'neutral' },
    ];
  });

  readonly outcomeIssueChips = computed<OutcomeIssueChip[]>(() => {
    const rows = new Map<string, { count: number; latestMessageId: string }>();
    for (const m of this.filtered()) {
      const topic = this.outcomeTopicForMessage(m);
      if (!topic) continue;
      const current = rows.get(topic);
      if (current) {
        current.count++;
      } else {
        rows.set(topic, { count: 1, latestMessageId: m.id });
      }
    }

    return OUTCOME_ISSUE_TOPICS
      .map(def => {
        const row = rows.get(def.topic);
        return row
          ? {
              testid: `observability-outcome-${def.topic}`,
              topic: def.topic,
              label: def.label,
              value: row.count,
              tone: def.tone,
              latestMessageId: row.latestMessageId,
            }
          : null;
      })
      .filter((chip): chip is OutcomeIssueChip => chip !== null);
  });

  readonly timelineLanes = computed<TimelineLane[]>(() => {
    const start = new Date(this.rangeStart()).getTime();
    const end = new Date(this.rangeEnd()).getTime();
    const span = Math.max(end - start, 1);
    const byParticipant = new Map<string, TimelineLane['marks'][number][]>();
    for (const m of this.filtered()) {
      const t = new Date(m.createdAt).getTime();
      const offsetPct = Math.max(0, Math.min(100, ((t - start) / span) * 100));
      const arr = byParticipant.get(m.participantId) ?? [];
      arr.push({
        id: m.id,
        offsetPct,
        kind: m.kind,
        severity: m.severity ?? null,
        title: `${m.participantId} · ${m.kind}${m.severity ? ' (' + m.severity + ')' : ''} · ${m.summary ?? ''}`,
      });
      byParticipant.set(m.participantId, arr);
    }
    return Array.from(byParticipant.entries())
      .sort((a, b) => (a[0] < b[0] ? -1 : 1))
      .map(([participantId, marks]) => ({ participantId, marks }));
  });

  readonly matrixKinds = computed(() => {
    const set = new Set<string>();
    for (const m of this.filtered()) set.add(m.kind);
    return Array.from(set).sort();
  });

  readonly matrixRows = computed<MatrixRow[]>(() => {
    const rows = new Map<string, { kindCounts: Partial<Record<string, number>>; total: number; jobs: Set<string> }>();
    for (const m of this.filtered()) {
      const row = rows.get(m.participantId) ?? { kindCounts: {}, total: 0, jobs: new Set<string>() };
      row.kindCounts[m.kind] = (row.kindCounts[m.kind] ?? 0) + 1;
      row.total++;
      if (m.jobId) row.jobs.add(m.jobId);
      rows.set(m.participantId, row);
    }
    return Array.from(rows.entries())
      .sort((a, b) => b[1].total - a[1].total)
      .map(([participantId, r]) => ({ participantId, kindCounts: r.kindCounts, total: r.total, jobs: r.jobs }));
  });

  readonly heatmapBuckets = computed<string[]>(() => {
    const start = new Date(this.rangeStart()).getTime();
    const end = new Date(this.rangeEnd()).getTime();
    const span = Math.max(end - start, 1);
    const out: string[] = [];
    for (let i = 0; i < HEATMAP_BUCKETS; i++) {
      const t = start + (span * (i + 0.5)) / HEATMAP_BUCKETS;
      out.push(this.shortBucket(new Date(t)));
    }
    return out;
  });

  readonly heatmapRows = computed(() => {
    const start = new Date(this.rangeStart()).getTime();
    const end = new Date(this.rangeEnd()).getTime();
    const span = Math.max(end - start, 1);
    const map = new Map<string, HeatmapCell[]>();
    for (const m of this.filtered()) {
      if (!m.tokens) continue;
      const t = new Date(m.createdAt).getTime();
      const bucket = Math.min(HEATMAP_BUCKETS - 1, Math.max(0, Math.floor(((t - start) / span) * HEATMAP_BUCKETS)));
      let arr = map.get(m.participantId);
      if (!arr) {
        arr = Array.from({ length: HEATMAP_BUCKETS }, (_, i) => ({
          participantId: m.participantId,
          bucket: i,
          tokens: 0,
          msgIds: [] as string[],
        }));
        map.set(m.participantId, arr);
      }
      const tokens = (m.tokens.input ?? 0) + (m.tokens.output ?? 0);
      arr[bucket].tokens += tokens;
      arr[bucket].msgIds.push(m.id);
    }
    const rows = Array.from(map.entries()).map(([participantId, cells]) => ({
      participantId,
      cells,
      total: cells.reduce((a, c) => a + c.tokens, 0),
    }));
    rows.sort((a, b) => b.total - a.total);
    return rows;
  });

  readonly heatmapHasData = computed(() => this.heatmapRows().length > 0);
  readonly heatmapTotalLabel = computed(() => {
    const rows = this.heatmapRows();
    const total = rows.reduce((a, r) => a + r.total, 0);
    return rows.length === 0
      ? 'No token messages in range.'
      : `${rows.length} participant${rows.length === 1 ? '' : 's'} · ${this.formatTokens(total)} tokens`;
  });

  readonly selectedMessage = computed<AgentMessage | null>(() => {
    const id = this.selectedMessageId();
    if (!id) return null;
    return this.messages().find(m => m.id === id) ?? null;
  });

  heatLevel(tokens: number): string {
    if (tokens <= 0) return 'l0';
    if (tokens < 1500) return 'l1';
    if (tokens < 5000) return 'l2';
    if (tokens < 15000) return 'l3';
    return 'l4';
  }

  formatTokens(n: number): string {
    if (!Number.isFinite(n)) return '0';
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M';
    if (n >= 1_000) return (n / 1_000).toFixed(1).replace(/\.0$/, '') + 'k';
    return String(Math.round(n));
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString('en-US');
    } catch {
      return iso;
    }
  }

  formatJson(msg: AgentMessage): string {
    try {
      return JSON.stringify(msg, null, 2);
    } catch {
      return String(msg);
    }
  }

  private shortBucket(d: Date): string {
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  }

  private outcomeTopicForMessage(m: AgentMessage): string | null {
    const haystack = [
      m.topic ?? '',
      m.summary ?? '',
      m.body ?? '',
      ...(m.tags ?? []),
    ].join(' ').toLowerCase();

    for (const def of OUTCOME_ISSUE_TOPICS) {
      if (haystack.includes(def.topic)) return def.topic;
    }

    return null;
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { TaskService } from '../../../../services/task.service';
import type { ProjectExpensiveJob, ProjectExpensiveJobsResponse, ProjectJobTokenDetail, ProjectPipelineCostTimeline, ProjectTokenCategory, ProjectTokenDataFreshness, ProjectTokenHeatmap, ProjectTokenHeatmapJob, ProjectTokenUsageSummary } from '../../../../features/project-token-usage';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ProjectPipelineCostTrendComponent } from '../project-pipeline-cost-trend/project-pipeline-cost-trend.component';
import { formatUsageTokens } from '../../../tokens';
interface CardSpec {
  testid: string;
  label: string;
  primary: number;
  secondary?: { label: string; value: number } | null;
  category: ProjectTokenCategory | 'total';
}

interface TimelineBucket {
  day: string;
  shortDay: string;
  total: number;
  calls: number;
  heightPct: number;
}

/** One step kind's window rollup for the legend + per-kind cost table. */
/**
 * Project Token Usage panel (slice 8 of the quality-system mockup,
 * docs/mockups/quality-system/, "Token Usage" surface). Renders the
 * project's lifetime + last-24h totals with the Job / Supporting /
 * Orchestrator category split (taxonomy.md vocabulary), a per-job ×
 * per-day heatmap, an expensive-jobs list, and a drill-down for one job
 * with per-run deltas.
 *
 * Action-driven principle: this panel does no analysis on its own. It
 * reads the canonical hybrid token aggregation via the `/api/projects/
 * {project}/token-usage/*` endpoints. Token usage is visibility, not enforcement
 * (Critical Boundaries in the README).
 *
 * Hide-when-empty: a project with no readable token entries
 * shows an explicit empty-state card instead of phantom zeros.
 */
@Component({
  selector: 'app-project-token-usage-panel',
  standalone: true,
  imports: [TooltipDirective, ProjectPipelineCostTrendComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-token-usage-panel.component.html',
  styleUrl: './project-token-usage-panel.component.scss',
})
export class ProjectTokenUsagePanelComponent {
  private readonly jobs = inject(TaskService);

  readonly projectName = input.required<string>();

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly summary = signal<ProjectTokenUsageSummary | null>(null);
  readonly heatmap = signal<ProjectTokenHeatmap | null>(null);
  readonly expensive = signal<ProjectExpensiveJob[]>([]);
  readonly pipelineCost = signal<ProjectPipelineCostTimeline | null>(null);

  readonly tokenFreshness = computed<ProjectTokenDataFreshness | null>(() => {
    const value = this.summary();
    if (!value) return null;
    return value.freshness ?? {
      status: 'complete',
      asOf: value.lastActivity,
      warning: null,
      sources: [],
    };
  });

  readonly selectedJobId = signal<string | null>(null);
  readonly drilldown = signal<ProjectJobTokenDetail | null>(null);
  readonly drilldownError = signal<string | null>(null);

  /** Hard cap on heatmap rows we render. Beyond this the panel scrolls. */
  private static readonly HEATMAP_TOP_ROWS = 12;

  readonly cards = computed<CardSpec[]>(() => {
    const s = this.summary();
    if (!s) return [];
    return [
      {
        testid: 'token-usage-card-total',
        label: 'Total tokens',
        primary: s.lifetimeTotalTokens,
        secondary: { label: 'Last 24h', value: s.last24hTotalTokens },
        category: 'total',
      },
      {
        testid: 'token-usage-card-job',
        label: 'Job tokens',
        primary: s.lifetimeJobTokens,
        secondary: { label: 'Last 24h', value: s.last24hJobTokens },
        category: 'job',
      },
      {
        testid: 'token-usage-card-supporting',
        label: 'Supporting jobs tokens',
        primary: s.lifetimeSupportingTokens,
        secondary: { label: 'Last 24h', value: s.last24hSupportingTokens },
        category: 'supporting',
      },
      {
        testid: 'token-usage-card-orchestrator',
        label: 'Orchestrator tokens',
        primary: s.lifetimeOrchestratorTokens,
        secondary: { label: 'Last 24h', value: s.last24hOrchestratorTokens },
        category: 'orchestrator',
      },
    ];
  });

  /** Top-N heatmap rows. Sorted by total descending by the backend. */
  readonly heatmapTopRows = computed<readonly ProjectTokenHeatmapJob[]>(() => {
    const h = this.heatmap();
    if (!h) return [];
    return h.jobs.slice(0, ProjectTokenUsagePanelComponent.HEATMAP_TOP_ROWS);
  });

  /** Per-day buckets folded across all heatmap rows (the timeline view). */
  readonly timelineBuckets = computed<TimelineBucket[]>(() => {
    const h = this.heatmap();
    if (!h || h.days.length === 0) return [];
    const totals = new Map<string, { total: number; calls: number }>();
    for (const day of h.days) totals.set(day, { total: 0, calls: 0 });
    for (const row of h.jobs) {
      for (const cell of row.cells) {
        const acc = totals.get(cell.day);
        if (!acc) continue;
        if (cell.total > 0) {
          acc.total += cell.total;
          // Calls aren't on the cell; approximate by row.calls × share of
          // the row this day represents. Keeps the tooltip honest enough
          // for an at-a-glance number.
          acc.calls += cell.total > 0 ? 1 : 0;
        }
      }
    }
    let max = 0;
    for (const { total } of totals.values()) if (total > max) max = total;
    return h.days.map(day => {
      const acc = totals.get(day) ?? { total: 0, calls: 0 };
      return {
        day,
        shortDay: this.shortDay(day),
        total: acc.total,
        calls: acc.calls,
        heightPct: max > 0 ? Math.max(2, Math.round((acc.total / max) * 100)) : 0,
      };
    });
  });

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (name) this.refresh(name);
    });

    effect(() => {
      const jobId = this.selectedJobId();
      const name = this.projectName();
      if (!jobId || !name) {
        this.drilldown.set(null);
        this.drilldownError.set(null);
        return;
      }
      this.drilldown.set(null);
      this.drilldownError.set(null);
      this.jobs.getProjectJobTokenDetail(name, jobId).subscribe({
        next: (d: ProjectJobTokenDetail) => this.drilldown.set(d),
        error: (err: HttpErrorResponse) => {
          this.drilldown.set(null);
          this.drilldownError.set(err?.error?.error ?? err.message ?? 'failed to load drill-down');
        },
      });
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.summary.set(null);
    this.heatmap.set(null);
    this.expensive.set([]);
    this.pipelineCost.set(null);
    this.selectedJobId.set(null);

    let pending = 4;
    const done = () => { pending--; if (pending <= 0) this.loading.set(false); };
    const fail = (err: HttpErrorResponse) => {
      this.loadError.set(this.loadError() ?? err?.message ?? 'unknown');
      done();
    };

    this.jobs.getProjectTokenUsageSummary(name).subscribe({
      next: (s: ProjectTokenUsageSummary) => { this.summary.set(s); done(); },
      error: fail,
    });
    this.jobs.getProjectTokenUsageHeatmap(name, 30).subscribe({
      next: (h: ProjectTokenHeatmap) => { this.heatmap.set(h); done(); },
      error: fail,
    });
    this.jobs.getProjectExpensiveJobs(name, 10).subscribe({
      next: (r: ProjectExpensiveJobsResponse) => { this.expensive.set(r.jobs); done(); },
      error: fail,
    });
    this.jobs.getProjectPipelineCost(name, 30).subscribe({
      next: (t: ProjectPipelineCostTimeline) => { this.pipelineCost.set(t); done(); },
      error: fail,
    });
  }

  onSelectJob(jobId: string): void {
    if (this.selectedJobId() === jobId) {
      this.selectedJobId.set(null);
    } else {
      this.selectedJobId.set(jobId);
    }
  }

  onClearSelection(): void {
    this.selectedJobId.set(null);
  }

  /** 5-bucket heat scale; computed against the row max so dense rows stay readable. */
  heatLevel(total: number): string {
    if (total <= 0) return 'n0';
    const max = this.maxCellValue();
    if (max <= 0) return 'n0';
    const ratio = total / max;
    if (ratio < 0.1) return 'n1';
    if (ratio < 0.3) return 'n2';
    if (ratio < 0.6) return 'n3';
    return 'n4';
  }

  private maxCellValue(): number {
    const h = this.heatmap();
    if (!h) return 0;
    let max = 0;
    for (const row of h.jobs) {
      for (const cell of row.cells) {
        if (cell.total > max) max = cell.total;
      }
    }
    return max;
  }

  readonly formatTokens = formatUsageTokens;

  formatTs(iso: string): string {
    if (!iso) return '';
    // Trim seconds + timezone for a tighter row.
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    const yyyy = d.getUTCFullYear();
    const mm = String(d.getUTCMonth() + 1).padStart(2, '0');
    const dd = String(d.getUTCDate()).padStart(2, '0');
    const hh = String(d.getUTCHours()).padStart(2, '0');
    const mi = String(d.getUTCMinutes()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd} ${hh}:${mi}`;
  }

  shortDay(day: string): string {
    // Day strings are YYYY-MM-DD; render MM-DD to keep the heatmap tight.
    if (day.length >= 10) return day.slice(5);
    return day;
  }

  timelineTooltip(bucket: TimelineBucket): string {
    const callsLabel = bucket.calls === 1 ? 'call' : 'calls';
    return `${bucket.day}: ${this.formatTokens(bucket.total)} tokens (${bucket.calls} ${callsLabel})`;
  }

  catGlyph(category: ProjectTokenCategory): string {
    switch (category) {
      case 'job': return '●';
      case 'supporting': return '◐';
      case 'orchestrator': return '◇';
    }
  }
}

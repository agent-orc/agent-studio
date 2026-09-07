import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import { ConceptHelpComponent } from '../../../../components/concept-help/concept-help.component';
import type { QuotaWindow } from '../../../../features/quota';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type {
  AdHocUsageAggregate,
  TokenSummaryAggregate,
  TokenTimeline,
  WorkspaceExpensiveJob,
} from '../../models/tokens.model';
import { canonicalUsageModelId } from '../../model-usage-table.util';
import { formatUsageCurrency, formatUsageTokens } from '../../usage-number-format.util';

interface SparkPoint {
  label: string;
  total: number;
  pct: number;
}

interface ProjectUsageRow {
  project: string;
  slug: string;
  orchestratorLlmCalls: number;
  totalTokens: number;
  estimatedApiCostUsd: number;
}

interface ModelUsageRow {
  model: string;
  source: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

/**
 * Full CLI-usage detail surface: routing headroom, token trend, per-CLI
 * quota windows / model spend / top sources, and the workspace's most
 * expensive tasks. Lives embedded inside the CLI-Management panel (the
 * "Settings-Dach"), reached from the status-bar usage modal's "Manage
 * usage caps" button. The status-bar quota strip itself opens a compact
 * per-CLI <app-cli-usage-modal> on click; this grouped surface is the
 * caps-editing roof behind it.
 *
 * Purely presentational - the shared `CliUsageStore` owns the polling
 * and feeds every input; `refreshAll` / `refreshOne` bubble back so the
 * host can re-probe.
 *
 * The "By project" rows are click-targets: each emits `openProjectSettings`
 * with the project name so the shell can route to that project's Settings
 * rail. Hover still shows the compact peek; the click is the only new
 * affordance (navigation stays shell-coordinated, never a leaf-side hash
 * write).
 */
@Component({
  selector: 'app-cli-usage-detail',
  standalone: true,
  imports: [ConceptHelpComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-usage-detail.html',
  styleUrl: './cli-usage-detail.scss',
})
export class CliUsageDetailComponent {
  /** Workspace mode keeps project attribution and task totals while account
   * windows live on the dedicated per-CLI pages. */
  readonly workspaceOnly = input(false);
  readonly quotaRows = input<CliUsageQuotaRow[]>([]);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly timeline24h = input<TokenTimeline | null>(null);
  readonly timeline7d = input<TokenTimeline | null>(null);
  readonly expensiveJobs = input<WorkspaceExpensiveJob[]>([]);
  readonly refreshing = input<Record<string, boolean>>({});
  readonly refreshingAll = input(false);

  readonly refreshAll = output<Event>();
  readonly refreshOne = output<{ cliType: CliType; event: Event }>();
  /** Project name whose Settings rail the shell should open on a row click. */
  readonly openProjectSettings = output<string>();

  /**
   * Per-project orchestrator spend, busiest first. Each row is the
   * click-target that bubbles `openProjectSettings` so the shell can route
   * to that project's Settings rail (`#/projects/<slug>/settings`).
   */
  readonly projectRows = computed<ProjectUsageRow[]>(() => {
    const rows = this.tokens()?.byProject ?? [];
    return rows
      .filter((p) => !!p.project)
      .map((p) => ({
        project: p.project,
        slug: this.toSlug(p.project),
        orchestratorLlmCalls: p.orchestratorLlmCalls,
        totalTokens:
          p.inputTokens + p.outputTokens + p.cacheReadTokens + p.cacheCreationTokens,
        estimatedApiCostUsd: p.estimatedApiCostUsd,
      }))
      .sort((a, b) => b.totalTokens - a.totalTokens);
  });

  /** Mirror of the shell's `toProjectSlug` so testids/deep-links line up. */
  private toSlug(name: string): string {
    return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  }

  headroom(row: CliUsageQuotaRow): string {
    if (row.primaryPct == null) return 'unknown';
    return `${Math.max(0, 100 - row.primaryPct)}%`;
  }

  projectTooltip(project: ProjectUsageRow): string {
    return `${project.project} · ${this.formatTokens(project.totalTokens)} tokens · ${project.orchestratorLlmCalls} calls · ${this.formatUsd(project.estimatedApiCostUsd)} - open project settings`;
  }

  limitText(window: QuotaWindow): string {
    if (window.used !== null && window.limit !== null) {
      return `${window.used} / ${window.limit}${window.unit ? ' ' + window.unit : ''}`;
    }
    return 'absolute values unavailable';
  }

  costLabel(value: number, priced: boolean): string {
    return priced ? this.formatUsd(value) : 'Unknown';
  }

  readonly formatTokens = formatUsageTokens;
  readonly formatUsd = formatUsageCurrency;

  modelRowsFor(cliType: CliType): ModelUsageRow[] {
    const rows: ModelUsageRow[] = [];
    for (const m of this.tokens()?.byModel ?? []) {
      const model = canonicalUsageModelId(m.modelId ?? m.model);
      if (!this.modelBelongsToCli(model, cliType)) continue;
      rows.push({
        model,
        source: 'orchestrator',
        calls: m.calls,
        inputTokens: m.inputTokens,
        outputTokens: m.outputTokens,
        cacheReadTokens: m.cacheReadTokens,
        cacheCreationTokens: m.cacheCreationTokens,
        estimatedApiCostUsd: m.estimatedApiCostUsd,
        modelPriced: m.modelPriced,
      });
    }
    for (const m of this.adhoc()?.byModel ?? []) {
      const model = canonicalUsageModelId(m.modelId ?? m.model);
      if (!this.modelBelongsToCli(model, cliType)) continue;
      rows.push({
        model,
        source: 'ad-hoc',
        calls: m.calls,
        inputTokens: m.inputTokens,
        outputTokens: m.outputTokens,
        cacheReadTokens: m.cacheReadTokens,
        cacheCreationTokens: m.cacheCreationTokens,
        estimatedApiCostUsd: m.estimatedApiCostUsd,
        modelPriced: m.modelPriced,
      });
    }
    return rows
      .sort((a, b) => this.totalTokens(b) - this.totalTokens(a))
      .slice(0, 5);
  }

  sourceRowsFor(cliType: CliType) {
    if (cliType !== 'claude') return [];
    return (this.adhoc()?.bySource ?? []).slice(0, 5);
  }

  topJobs(): WorkspaceExpensiveJob[] {
    return this.expensiveJobs().slice(0, 6);
  }

  spark24h(): SparkPoint[] {
    return this.sparkByBucket(this.timeline24h(), 24);
  }

  spark7d(): SparkPoint[] {
    const timeline = this.timeline7d();
    if (!timeline) return [];
    const byDay = new Map<string, number>();
    for (const cell of timeline.cells) {
      const key = (cell.bucketStart || '').slice(0, 10);
      if (!key) continue;
      byDay.set(key, (byDay.get(key) ?? 0) + cell.total);
    }
    return this.toSparkPoints(Array.from(byDay.entries()).slice(-7));
  }

  totalTokens(row: ModelUsageRow): number {
    return row.inputTokens + row.outputTokens + row.cacheReadTokens + row.cacheCreationTokens;
  }

  private sparkByBucket(timeline: TokenTimeline | null, limit: number): SparkPoint[] {
    if (!timeline) return [];
    const byBucket = new Map<string, number>();
    for (const cell of timeline.cells) {
      byBucket.set(cell.bucketStart, (byBucket.get(cell.bucketStart) ?? 0) + cell.total);
    }
    return this.toSparkPoints(Array.from(byBucket.entries()).slice(-limit));
  }

  private toSparkPoints(entries: [string, number][]): SparkPoint[] {
    const max = Math.max(1, ...entries.map(([, total]) => total));
    return entries.map(([label, total]) => ({ label, total, pct: Math.max(3, (total / max) * 100) }));
  }

  private modelBelongsToCli(model: string, cliType: CliType): boolean {
    const m = (model ?? '').toLowerCase();
    switch (cliType) {
      case 'claude':
        return m.includes('claude') || m.includes('haiku') || m.includes('sonnet') || m.includes('opus');
      case 'codex':
        return m.includes('codex') || m.startsWith('gpt') || /^o\d/.test(m);
      case 'gemini':
        return m.includes('gemini');
      default:
        return false;
    }
  }
}

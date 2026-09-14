import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
  OnInit,
  OnDestroy,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { RegressionRadarResult, RegressionRadarTaskGroup, SpecChangeEntry } from '../../models/regression-radar.model';
import { TaskService } from '../../../../services/task.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { InfoButtonComponent } from '../../../../components/info-button/info-button.component';

@Component({
  selector: 'app-regression-radar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, TooltipDirective, InfoButtonComponent],
  templateUrl: './regression-radar.component.html',
  styleUrl: './regression-radar.component.scss',
})
export class RegressionRadarComponent implements OnInit, OnDestroy {
  readonly scope = input<'task' | 'project'>('task');
  /**
   * 'panel' is the standalone card used on the project hub. 'inline' embeds the
   * radar inside a pane that already owns the surrounding section frame, so it
   * drops its own card border and heading weight.
   */
  readonly variant = input<'panel' | 'inline'>('panel');
  readonly jobId = input<string | null>(null);
  readonly projectName = input<string | null>(null);
  readonly watchPath = input<string>();

  private readonly jobService = inject(TaskService);
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  readonly result = signal<RegressionRadarResult | null>(null);
  readonly loading = signal(true);
  readonly expanded = signal<string | null>(null);
  /** Whether the per-file detail list is revealed. Compact (collapsed) by default. */
  readonly detailsOpen = signal(false);

  private static readonly SEVERITY_ORDER: Record<string, number> = {
    Drift: 0,
    AtRisk: 1,
    Intended: 2,
  };

  /** Entries ordered most-severe first so Drift/AtRisk surface above Intended. */
  readonly sortedEntries = computed(() => {
    const r = this.result();
    if (!r) return [];
    const order = RegressionRadarComponent.SEVERITY_ORDER;
    return [...r.entries].sort(
      (a, b) => (order[a.category] ?? 99) - (order[b.category] ?? 99),
    );
  });

  readonly sortedTaskGroups = computed(() => {
    const r = this.result();
    if (!r?.taskGroups?.length) return [];
    const order = RegressionRadarComponent.SEVERITY_ORDER;
    return [...r.taskGroups]
      .map(group => ({
        ...group,
        entries: [...group.entries].sort(
          (a, b) => (order[a.category] ?? 99) - (order[b.category] ?? 99),
        ),
      }))
      .sort((a, b) => this.groupSeverity(a) - this.groupSeverity(b));
  });

  /** Short first..last attributed commit label, or null when unavailable. */
  readonly shaRange = computed(() => {
    const r = this.result();
    if (!r || !r.baselineSha || !r.headSha) return null;
    return `${r.baselineSha.slice(0, 7)}..${r.headSha.slice(0, 7)}`;
  });

  readonly fullShaRange = computed(() => {
    const r = this.result();
    if (!r || !r.baselineSha || !r.headSha) return '';
    return `${r.baselineSha} .. ${r.headSha}`;
  });

  readonly title = computed(() =>
    this.scope() === 'project' ? 'Project Regression Radar' : 'Regression Radar',
  );

  readonly emptyLabel = computed(() =>
    this.scope() === 'project'
      ? 'No spec file changes in attributed project commits'
      : 'No changes for this task',
  );

  /** Human-readable generation duration, e.g. "420 ms" or "1.3 s". */
  readonly durationLabel = computed(() => {
    const r = this.result();
    if (!r) return null;
    const ms = r.durationMs;
    if (ms < 1000) return `${ms} ms`;
    return `${(ms / 1000).toFixed(1)} s`;
  });

  /** Relative "generated ... ago" label, recomputed on each 30s reload. */
  readonly generatedAtLabel = computed(() => {
    const r = this.result();
    if (!r?.generatedAt) return null;
    const then = new Date(r.generatedAt).getTime();
    if (Number.isNaN(then)) return null;
    const secs = Math.max(0, Math.round((Date.now() - then) / 1000));
    if (secs < 5) return 'just now';
    if (secs < 60) return `${secs}s ago`;
    const mins = Math.round(secs / 60);
    if (mins < 60) return `${mins}m ago`;
    const hours = Math.round(mins / 60);
    return `${hours}h ago`;
  });

  readonly generatedAtTooltip = computed(() => {
    const r = this.result();
    if (!r?.generatedAt) return '';
    const d = new Date(r.generatedAt);
    if (Number.isNaN(d.getTime())) return '';
    return `Generated ${d.toLocaleString()}`;
  });

  readonly hasData = computed(() => {
    const r = this.result();
    return r !== null && !r.error && r.totalSpecChanges > 0;
  });

  readonly isEmpty = computed(() => {
    const r = this.result();
    return r !== null && !r.error && r.totalSpecChanges === 0;
  });

  readonly statusIcon = computed(() => {
    const r = this.result();
    if (!r || r.error || r.totalSpecChanges === 0) return null;
    if (r.driftCount > 0) return { icon: 'drift', label: 'Drift detected', tone: 'danger' };
    if (r.atRiskCount > 0) return { icon: 'review', label: 'Review needed', tone: 'warning' };
    return { icon: 'ok', label: 'All intended', tone: 'success' };
  });

  ngOnInit(): void {
    this.load();
    this.refreshTimer = setInterval(() => this.load(), 30_000);
  }

  ngOnDestroy(): void {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  load(): void {
    const request = this.scope() === 'project'
      ? this.jobService.getProjectRegressionRadar(this.projectName() ?? '')
      : this.jobService.getRegressionRadar(this.jobId() ?? '', this.watchPath());

    request.subscribe({
      next: (r) => { this.result.set(r); this.loading.set(false); },
      error: () => { this.loading.set(false); },
    });
  }

  toggleExpand(path: string): void {
    this.expanded.update(v => v === path ? null : path);
  }

  toggleDetails(): void {
    this.detailsOpen.update(v => !v);
  }

  categoryIcon(entry: SpecChangeEntry): string {
    switch (entry.category) {
      case 'Intended': return 'intended';
      case 'AtRisk':   return 'at-risk';
      case 'Drift':    return 'drift';
      default:         return '';
    }
  }

  categoryLabel(entry: SpecChangeEntry): string {
    switch (entry.category) {
      case 'Intended': return 'Intended';
      case 'AtRisk':   return 'At Risk';
      case 'Drift':    return 'Drift';
      default:         return entry.category;
    }
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'A': return 'added';
      case 'D': return 'deleted';
      case 'M': return 'modified';
      default:
        if (status.startsWith('R')) return 'renamed';
        return status;
    }
  }

  taskGroupLabel(group: RegressionRadarTaskGroup): string {
    return group.jobTitle?.trim() || group.jobId;
  }

  taskGroupSummary(group: RegressionRadarTaskGroup): string {
    const parts: string[] = [];
    if (group.driftCount) parts.push(`${group.driftCount} drift`);
    if (group.atRiskCount) parts.push(`${group.atRiskCount} at risk`);
    if (group.intendedCount) parts.push(`${group.intendedCount} intended`);
    return parts.join(' · ') || 'No spec changes';
  }

  entryKey(entry: SpecChangeEntry): string {
    return `${entry.jobId ?? 'task'}:${entry.path}`;
  }

  private groupSeverity(group: RegressionRadarTaskGroup): number {
    if (group.driftCount > 0) return 0;
    if (group.atRiskCount > 0) return 1;
    return 2;
  }
}

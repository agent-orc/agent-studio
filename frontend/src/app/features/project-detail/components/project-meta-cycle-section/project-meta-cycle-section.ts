import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { SupervisorService } from '../../../../services/supervisor.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import {
  MetaCycleActionKind,
  MetaCycleConfigDto,
  MetaCycleReport,
  MetaCycleVerdict,
} from '../../../../models/supervisor.model';

/**
 * Project-level meta-cycle panel: shows whether the per-project pause-inspect-resume
 * loop is enabled, the last cycle's verdict + action, and the trailing history of
 * cycle reports. Read-only in this first cut: the panel reflects what the
 * `MetaCycleHostedService` writes to disk via `/api/supervisor/{project}/meta-cycle`.
 *
 * The full design is in `docs/mockups/orchestrator-meta-cycle/` and ADR-0022.
 *
 * Polls every 10 s while mounted.
 */
@Component({
  selector: 'app-project-meta-cycle-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-meta-cycle-section.html',
  styleUrl: './project-meta-cycle-section.scss'
})
export class ProjectMetaCycleSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly svc = inject(SupervisorService);
  private timer?: VisibleIntervalHandle;

  readonly enabled = signal<boolean>(false);
  readonly config = signal<MetaCycleConfigDto | null>(null);
  readonly reports = signal<MetaCycleReport[]>([]);
  readonly loading = signal<boolean>(false);

  readonly lastReport = computed<MetaCycleReport | null>(() => this.reports()[0] ?? null);
  readonly lastVerdict = computed<MetaCycleVerdict | null>(() => this.lastReport()?.verdict ?? null);
  readonly lastActionKind = computed<MetaCycleActionKind | null>(() => this.lastReport()?.action.kind ?? null);
  readonly lastReason = computed<string>(() => this.lastReport()?.action.reason ?? '');
  readonly lastCompletedAt = computed<string | null>(() => this.lastReport()?.completedAt ?? null);
  readonly lastFindings = computed(() => this.lastReport()?.findings ?? []);

  readonly statusLabel = computed<string>(() => {
    if (!this.enabled()) return 'off';
    const v = this.lastVerdict();
    if (v == null) return 'idle';
    return this.verdictLabel(v);
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), 10_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  refresh(): void {
    const project = this.projectName();
    if (!project) return;
    this.loading.set(true);
    this.svc.metaCycle(project, 8).subscribe({
      next: (resp) => {
        this.enabled.set(resp.enabled);
        this.config.set(resp.config);
        // Endpoint already returns newest-first.
        this.reports.set(resp.reports);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); },
    });
  }

  verdictLabel(v: MetaCycleVerdict | null): string {
    switch (v) {
      case 'healthy': return 'healthy';
      case 'fixTriggering': return 'fix queued';
      case 'escalationOnly': return 'escalated';
      case 'aborted': return 'aborted';
      default: return 'idle';
    }
  }

  actionLabel(k: MetaCycleActionKind | null): string {
    switch (k) {
      case 'resume': return 'resume';
      case 'updateStableThenResume': return 'update-stable + resume';
      case 'queueFix': return 'queue-fix';
      case 'escalateToUser': return 'escalate-to-user';
      case 'noOp': return 'no-op';
      default: return '';
    }
  }

  formatRelative(iso: string | null): string {
    if (!iso) return '—';
    const t = Date.parse(iso);
    if (isNaN(t)) return iso;
    const seconds = Math.max(0, Math.floor((Date.now() - t) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    return `${Math.floor(seconds / 3600)}h ago`;
  }

  formatTimeShort(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleTimeString('en-US');
    } catch {
      return iso;
    }
  }
}

import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AnalysisReportService } from '../../../../services/analysis-report.service';
import {
  ANALYSIS_CADENCES,
  ANALYSIS_TOPICS,
  AnalysisReport,
} from '../../../../models/analysis-report.model';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Project-level Analysis Reports surface (ROADMAP "Analysis Reports and
 * Meta-Actions"). Three things in one section:
 *
 * 1. Manual-trigger buttons for the fixed topic catalogue (roadmap alignment,
 *    queue health, docs drift, stale jobs, token spend, QA status).
 * 2. Scheduling controls per topic, default <code>disabled</code>. The cadence
 *    is persisted in <code>ProjectSettings.AnalysisSchedules</code>; the
 *    backend does not auto-run scheduled reports yet, see
 *    <code>docs/system/reports/analysis-reports.md</code>.
 * 3. Report history newest-first with title, scope, producer, trigger,
 *    severity, parse status and a count of follow-ups. Click a row to
 *    drill down (event surfaced through <code>(openReport)</code>).
 *
 * Polls every 10 s while mounted. Empty state is intentional and explicit:
 * the user should see the surface even when nothing has been run yet.
 */
@Component({
  selector: 'app-project-analysis-reports-section',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-analysis-reports-section.html',
  styleUrl: './project-analysis-reports-section.scss'
})
export class ProjectAnalysisReportsSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  readonly openReport = output<AnalysisReport>();

  private readonly svc = inject(AnalysisReportService);
  private timer?: VisibleIntervalHandle;

  readonly topics = ANALYSIS_TOPICS;
  readonly cadences = ANALYSIS_CADENCES;

  readonly reports = signal<AnalysisReport[]>([]);
  readonly schedule = signal<Record<string, string>>({});
  readonly loading = signal<boolean>(false);
  readonly triggering = signal<string | null>(null);
  readonly lastError = signal<string | null>(null);

  readonly hasUnstructured = computed(() => this.reports().some(r => r.parseStatus !== 'Structured'));

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(true), 10_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  refresh(silent = false): void {
    void silent;
    const project = this.projectName();
    if (!project) return;
    this.loading.set(true);
    this.svc.list(project, { limit: 100 }).subscribe({
      next: (resp) => {
        this.reports.set(resp.reports ?? []);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); },
    });
    this.svc.getSchedule(project).subscribe({
      next: (map) => this.schedule.set(map ?? {}),
      error: () => { /* silent; keep last */ },
    });
  }

  scheduleFor(topic: string): string {
    return this.schedule()[topic] ?? 'disabled';
  }

  onScheduleChange(topic: string, cadence: string): void {
    const project = this.projectName();
    if (!project) return;
    // Optimistic update so the select reflects the choice immediately.
    this.schedule.update(m => ({ ...m, [topic]: cadence }));
    this.svc.setSchedule(project, topic, cadence).subscribe({
      next: (m) => this.schedule.set(m ?? {}),
      error: () => { /* leave optimistic value; refresh will reconcile */ },
    });
  }

  trigger(topic: string): void {
    const project = this.projectName();
    if (!project) return;
    this.triggering.set(topic);
    this.lastError.set(null);
    this.svc.trigger(project, topic).subscribe({
      next: () => {
        this.triggering.set(null);
        this.refresh(true);
      },
      error: (err) => {
        this.triggering.set(null);
        this.lastError.set(err?.error?.error ?? 'Failed to run inspection.');
      },
    });
  }

  triggerLabel(t: string): string {
    switch (t) {
      case 'Manual': return 'manual';
      case 'Scheduled': return 'scheduled';
      case 'MetaCycle': return 'meta-cycle';
      case 'SupportingAgent': return 'supporting-agent';
      case 'ExternalMonitor': return 'external-monitor';
      default: return t.toLowerCase();
    }
  }

  parseLabel(s: string): string {
    return s === 'MalformedJson' ? 'malformed JSON' : 'unstructured';
  }

  parseTooltip(r: AnalysisReport): string {
    if (r.parseStatus === 'MalformedJson')
      return `Sidecar JSON failed to parse: ${r.parseError ?? 'unknown error'}. The Markdown body is still readable.`;
    if (r.parseStatus === 'Unstructured')
      return 'No JSON sidecar was written. The Markdown body is still readable.';
    return '';
  }

  scopeLabel(r: AnalysisReport): string {
    const k = r.scope?.kind ?? 'Project';
    if (k === 'Task' && r.scope?.jobId) return `task / ${r.scope.jobId}`;
    if (k === 'Run' && r.scope?.jobId) return `run / ${r.scope.jobId}#${r.scope.runIndex ?? '?'}`;
    if (k === 'Workspace') return 'workspace';
    if (k === 'TimeWindow') return 'time-window';
    return 'project';
  }

  producerLabel(r: AnalysisReport): string {
    const k = r.producer?.kind ?? 'Manual';
    return k.charAt(0).toLowerCase() + k.slice(1);
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
}

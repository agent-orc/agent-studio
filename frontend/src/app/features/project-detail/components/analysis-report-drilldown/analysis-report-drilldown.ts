import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { AnalysisReportService } from '../../../../services/analysis-report.service';
import { AnalysisReport, AnalysisReportReference } from '../../../../models/analysis-report.model';

/**
 * Drill-down overlay for one analysis report. Renders the full Markdown body,
 * the structured JSON, and the typed reference list. Markdown is the durable
 * human artifact: it stays visible even when the JSON sidecar is unstructured
 * or malformed (the warning is shown alongside, not in place of, the body).
 *
 * Reference rows do not auto-resolve to deep links in this first cut; they
 * carry the stable id strings (job, run, commit, screenshot path, bus message,
 * runtime event, previous report, log slice, doc) so the user can navigate by
 * hand or copy them into another tool. The Agent Message Bus and runtime
 * surfaces are linked via the existing project-page entry points; this view
 * does not duplicate those timelines.
 */
@Component({
  selector: 'app-analysis-report-drilldown',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './analysis-report-drilldown.html',
  styleUrl: './analysis-report-drilldown.scss'
})
export class AnalysisReportDrilldownComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly reportId = input.required<string>();
  readonly closeRequest = output<void>();

  private readonly svc = inject(AnalysisReportService);

  readonly report = signal<AnalysisReport | null>(null);
  readonly markdown = signal<string | null>(null);
  readonly loading = signal<boolean>(false);

  ngOnInit(): void {
    this.fetch();
  }

  fetch(): void {
    const project = this.projectName();
    const id = this.reportId();
    if (!project || !id) return;
    this.loading.set(true);
    this.svc.get(project, id).subscribe({
      next: (resp) => {
        this.report.set(resp.report);
        this.markdown.set(resp.markdown);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); },
    });
  }

  jsonView(): string {
    const r = this.report();
    if (!r) return '';
    try { return JSON.stringify(r, null, 2); } catch { return String(r); }
  }

  scopeLabel(): string {
    const r = this.report();
    if (!r) return '';
    const k = r.scope?.kind ?? 'Project';
    if (k === 'Task' && r.scope?.jobId) return `task / ${r.scope.jobId}`;
    if (k === 'Run' && r.scope?.jobId) return `run / ${r.scope.jobId}#${r.scope.runIndex ?? '?'}`;
    if (k === 'Workspace') return 'workspace';
    if (k === 'TimeWindow') return 'time-window';
    return `project / ${r.scope?.project ?? ''}`;
  }

  producerLabel(): string {
    const r = this.report();
    if (!r) return '';
    const p = r.producer;
    const parts: string[] = [p?.kind ?? 'Manual'];
    if (p?.agent) parts.push(p.agent);
    if (p?.participantId) parts.push(`@${p.participantId}`);
    return parts.join(' · ');
  }

  formatTime(iso: string | null | undefined): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString('en-US');
    } catch {
      return iso;
    }
  }

  /** Helper used by reference renderers in case future overlays want to copy the id. */
  refId(ref: AnalysisReportReference): string { return ref.ref; }
}

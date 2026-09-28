import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { DriftService } from '../../../../services/drift.service';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  DriftDimension,
  DriftDimensionType,
  DriftFinding,
  DriftFollowUpSuggestion,
  DriftReport,
  DriftReportDetailResponse,
  DriftScoreBand,
  DriftSeverity,
} from '../../../../models/drift.model';
import { TaskState } from '../../../../models/task.model';

/**
 * Project-level Drift overview surface (ROADMAP "Drift Control",
 * design-principles "Drift is a scored project dimension"). Sits above the
 * existing architecture-marble section and answers four scan-level
 * questions for the latest drift report:
 *
 *  - overall score and band
 *  - dimension state (Intent, Spec, TaskJob, Architecture, Documentation,
 *    Marketing, Design, Test, Runtime, Process, Schema, Token)
 *  - latest findings and follow-up suggestions
 *  - report history with parse-status badges
 *
 * Action buttons cover the seven named drift comparisons; ADR / Code Drift
 * and Docs / Marketing Drift hit dedicated backend producers, the rest queue
 * a normal preparation task with a templated drift-analysis prompt so
 * "follow-up work becomes a normal queued task" stays the rule.
 *
 * Constraint: scores never hide evidence. Failed JSON sidecars surface a
 * warning chip while keeping the Markdown drilldown reachable; an empty
 * pile keeps the action buttons visible so the user can start one.
 */

interface DimensionCardRow {
  type: DriftDimensionType;
  label: string;
  dimension: DriftDimension | null;
}

const DIMENSION_LABELS: Record<DriftDimensionType, string> = {
  Intent: 'Intent',
  Spec: 'Spec',
  TaskJob: 'Task / Job',
  Architecture: 'Architecture',
  Documentation: 'Documentation',
  Marketing: 'Marketing',
  Design: 'Design',
  Test: 'Test',
  Runtime: 'Runtime',
  Process: 'Process',
  Schema: 'Schema',
  Token: 'Token',
};

const ALL_DIMENSIONS: DriftDimensionType[] = [
  'Intent', 'Spec', 'TaskJob', 'Architecture', 'Documentation', 'Marketing',
  'Design', 'Test', 'Runtime', 'Process', 'Schema', 'Token',
];

interface ActionButton {
  slug: string;
  label: string;
  description: string;
  /**
   * `inline` fires the matching backend POST and produces an evidence-only
   * drift report immediately. `queue` creates a 1-preparation task whose
   * prompt asks an agent to run the comparison; the returned job id is
   * surfaced for the user to promote when ready.
   */
  kind: 'inline' | 'queue';
  /** When kind=='queue', label of the relatedDimension on the prompt. */
  relatedDimension?: DriftDimensionType;
}

const ACTIONS: ActionButton[] = [
  { slug: 'analyze-project',  label: 'Analyze Project Drift',                  description: 'Score every drift dimension at once and write a project-level report.', kind: 'queue' },
  { slug: 'specs-tasks-jobs', label: 'Compare Specs to Tasks and Jobs',        description: 'Verify queue and job evidence still match written specs and prompts.', kind: 'queue', relatedDimension: 'Spec' },
  { slug: 'adrs-code',        label: 'Compare ADRs to Code',                   description: 'Run the ADR / Code Drift action against the current source tree.', kind: 'inline' },
  { slug: 'docs-marketing',   label: 'Compare Docs and Marketing to Product',  description: 'Run the Docs / Marketing Drift action against canonical project docs.', kind: 'inline' },
  { slug: 'design-screenshots', label: 'Compare Design to Screenshots',        description: 'Compare design references against the latest captured screenshots.', kind: 'queue', relatedDimension: 'Design' },
  { slug: 'tests-risk',       label: 'Compare Tests to Risk',                  description: 'Check whether tests still cover the documented risk areas.', kind: 'queue', relatedDimension: 'Test' },
  { slug: 'runtime-expectations', label: 'Compare Runtime to Expectations',    description: 'Compare runtime logs and observability against expected behaviour.', kind: 'queue', relatedDimension: 'Runtime' },
];

@Component({
  selector: 'app-project-drift-overview-section',
  standalone: true,
  imports: [TooltipDirective, OverlayPortalDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-drift-overview-section.html',
  styleUrl: './project-drift-overview-section.scss'
})
export class ProjectDriftOverviewSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly drift = inject(DriftService);
  private readonly jobs = inject(TaskService);

  readonly DIMENSION_LABELS = DIMENSION_LABELS;
  readonly actions = ACTIONS;

  readonly reports = signal<DriftReport[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly busyAction = signal<string | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busyFollowUp = signal<string | null>(null);
  readonly openedDimensionType = signal<DriftDimensionType | null>(null);
  readonly reportDetail = signal<DriftReportDetailResponse | null>(null);

  readonly latest = computed<DriftReport | null>(() => {
    const list = this.reports();
    return list.length > 0 ? list[0] : null;
  });

  readonly dimensionRows = computed<DimensionCardRow[]>(() => {
    const r = this.latest();
    const byType = new Map<DriftDimensionType, DriftDimension>();
    if (r) for (const d of r.dimensions) {
      const normalized = normalizeDimensionType(d.type);
      if (normalized) byType.set(normalized, { ...d, type: normalized });
    }
    return ALL_DIMENSIONS.map(t => ({
      type: t,
      label: DIMENSION_LABELS[t],
      dimension: byType.get(t) ?? null,
    }));
  });

  readonly openedDimension = computed<DimensionCardRow | null>(() => {
    const t = this.openedDimensionType();
    if (!t) return null;
    return this.dimensionRows().find(r => r.type === t && r.dimension) ?? null;
  });

  private timer?: VisibleIntervalHandle;

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(true), 15_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  refresh(silent = false): void {
    const project = this.projectName();
    if (!project) return;
    if (!silent) this.loading.set(true);
    this.drift.listReports(project, { limit: 50 }).subscribe({
      next: (resp) => {
        try {
          const list = Array.isArray(resp?.reports) ? resp.reports : [];
          this.reports.set(list);
          this.error.set(null);
        } catch (e) {
          this.error.set(this.describe(e, 'Could not parse drift report list.'));
        }
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describe(err, 'Drift API call failed.'));
        this.loading.set(false);
      },
    });
  }

  toggleDimension(type: DriftDimensionType): void {
    this.openedDimensionType.update(curr => curr === type ? null : type);
  }

  closeDimension(): void {
    this.openedDimensionType.set(null);
  }

  openReport(reportId: string): void {
    const project = this.projectName();
    if (!project || !reportId) return;
    this.drift.getReport(project, reportId).subscribe({
      next: (resp) => this.reportDetail.set(resp ?? null),
      error: (err) => this.actionError.set(this.describe(err, 'Could not open drift report.')),
    });
  }

  closeReport(ev?: Event): void {
    if (ev && ev.target && (ev.target as HTMLElement).closest('.pdov__report-card')) return;
    this.reportDetail.set(null);
  }

  stop(ev: Event): void { ev.stopPropagation(); }

  runAction(action: ActionButton): void {
    const project = this.projectName();
    if (!project) return;
    this.busyAction.set(action.slug);
    this.actionMessage.set(null);
    this.actionError.set(null);
    if (action.kind === 'inline' && action.slug === 'adrs-code') {
      this.drift.runAdrCodeDrift(project).subscribe({
        next: (resp) => {
          this.busyAction.set(null);
          this.actionMessage.set(`ADR / Code drift evidence captured (report ${resp?.report?.reportId ?? '…'}).`);
          this.refresh(true);
        },
        error: (err) => {
          this.busyAction.set(null);
          this.actionError.set(this.describe(err, 'ADR / Code drift action failed.'));
        },
      });
      return;
    }
    if (action.kind === 'inline' && action.slug === 'docs-marketing') {
      this.drift.runDocsMarketingDrift(project).subscribe({
        next: (resp) => {
          this.busyAction.set(null);
          this.actionMessage.set(`Docs / Marketing drift evidence captured (report ${resp?.report?.reportId ?? '…'}).`);
          this.refresh(true);
        },
        error: (err) => {
          this.busyAction.set(null);
          this.actionError.set(this.describe(err, 'Docs / Marketing drift action failed.'));
        },
      });
      return;
    }
    // queue: create a 1-preparation task with a templated drift-analysis prompt.
    this.queueDriftAnalysisTask(project, action);
  }

  private queueDriftAnalysisTask(project: string, action: ActionButton): void {
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyAction.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `drift-${action.slug}-${Date.now().toString(36)}`;
        const promptMarkdown = this.buildDriftPrompt(action);
        this.jobs.createJob({
          id: slug,
          title: action.label,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: TaskState.Preparation,
        }).subscribe({
          next: (resp) => {
            this.busyAction.set(null);
            this.actionMessage.set(`Queued ${resp?.id ?? slug} in 1-preparation. Promote to 2-ready when scoped.`);
          },
          error: (err) => {
            this.busyAction.set(null);
            this.actionError.set(this.describe(err, 'Could not queue drift analysis task.'));
          },
        });
      },
      error: () => {
        this.busyAction.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  createFollowUpFromFinding(dim: DriftDimension, finding: DriftFinding): void {
    const project = this.projectName();
    if (!project) return;
    this.busyFollowUp.set(finding.findingId);
    this.actionMessage.set(null);
    this.actionError.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyFollowUp.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `followup-drift-${dimSlugStatic(dim.type)}-${finding.findingId.slice(0, 16)}-${Date.now().toString(36)}`.slice(0, 100);
        const title = `Follow-up: ${DIMENSION_LABELS[dim.type]} drift - ${truncate(finding.summary, 60)}`;
        const promptMarkdown = this.buildFindingFollowUpPrompt(dim, finding);
        this.jobs.createJob({
          id: slug,
          title,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: TaskState.Preparation,
        }).subscribe({
          next: (resp) => {
            this.busyFollowUp.set(null);
            this.actionMessage.set(`Queued follow-up task ${resp?.id ?? slug} in 1-preparation.`);
          },
          error: (err) => {
            this.busyFollowUp.set(null);
            this.actionError.set(this.describe(err, 'Could not queue follow-up task.'));
          },
        });
      },
      error: () => {
        this.busyFollowUp.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  createFollowUpFromSuggestion(s: DriftFollowUpSuggestion): void {
    const project = this.projectName();
    if (!project) return;
    this.busyFollowUp.set(s.title);
    this.actionMessage.set(null);
    this.actionError.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyFollowUp.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const newSlug = `followup-drift-${toSlug(s.title).slice(0, 40)}-${Date.now().toString(36)}`;
        const title = truncate(s.title, 90);
        const promptMarkdown = this.buildSuggestionFollowUpPrompt(s);
        this.jobs.createJob({
          id: newSlug,
          title,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: (s.targetState === TaskState.Ready ? TaskState.Ready : TaskState.Preparation),
        }).subscribe({
          next: (resp) => {
            this.busyFollowUp.set(null);
            this.actionMessage.set(`Queued follow-up task ${resp?.id ?? newSlug}.`);
          },
          error: (err) => {
            this.busyFollowUp.set(null);
            this.actionError.set(this.describe(err, 'Could not queue follow-up task.'));
          },
        });
      },
      error: () => {
        this.busyFollowUp.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  private resolveWatchPath(project: string): Observable<string | null> {
    return this.jobs.getWatchPaths().pipe(map(entries => {
      const match = entries.find(e => e.name === project)
        ?? entries.find(e => e.path === project);
      return match?.path ?? null;
    }));
  }

  private buildDriftPrompt(action: ActionButton): string {
    return `# ${action.label}

Spawned from the project Drift overview surface.

## What to do

${action.description}

Produce a Markdown report plus an inline JSON block conforming to
\`docs/app/schemas/drift-report.schema.json\`. POST the reply back through the
appropriate \`/api/drift/{project}/actions/...\` endpoint when one exists,
or attach the report to this task's \`status.md\` for review.

The orchestrator must not move source state on the strength of this
report; produce evidence only.
`;
  }

  private buildFindingFollowUpPrompt(dim: DriftDimension, finding: DriftFinding): string {
    const refs = (finding.evidenceRefs ?? []).map(r => `- \`${r}\``).join('\n');
    return `# Drift follow-up: ${DIMENSION_LABELS[dim.type]} - ${truncate(finding.summary, 80)}

Created from the project Drift overview.

**Dimension:** ${dim.type}
**Finding id:** ${finding.findingId}
**Severity:** ${finding.severity}
**Status:** ${finding.status}

## Finding

> ${finding.summary}

## Evidence
${refs || '_(no evidence refs on the finding)_'}

## What to do

Confirm the drift, list affected files, and decide whether the work belongs
in a single task or several. Promote to \`2-ready\` only after the scope is
clear and the change is bounded.
`;
  }

  private buildSuggestionFollowUpPrompt(s: DriftFollowUpSuggestion): string {
    return `# Drift follow-up: ${s.title}

Created from the project Drift overview suggestions.

**Priority:** ${s.priority}
${s.relatedDimension ? `**Related dimension:** ${s.relatedDimension}\n` : ''}

## Summary

${s.summary ?? '_(no summary on suggestion)_'}

## What to do

Refine and bound this scope, then promote when ready. Drift is a triage
signal; do not let the suggestion text replace evidence-based scoping.
`;
  }

  // ----------------------------------------------------------------------
  // Display helpers
  // ----------------------------------------------------------------------

  /** Display helper: render an enum-style string with a leading capital,
   *  regardless of wire case. The backend serializes drift enums lowercase
   *  ('warn', 'high', 'manual', 'schema') even though the schema and code
   *  are PascalCase; the UI needs a single normalized presentation. */
  tc(value: string | null | undefined): string { return titleCase(value); }

  /** Case-insensitive parseStatus equality. Wire is lowercase ('structured'),
   *  comparisons in templates target the PascalCase enum names. */
  parseStatusEq(actual: string | null | undefined, expected: string): boolean {
    return (actual ?? '').toString().toLowerCase() === expected.toLowerCase();
  }

  parseStatusClass(actual: string | null | undefined): string {
    return (actual ?? '').toString().toLowerCase();
  }

  bandClass(b: DriftScoreBand | string | undefined | null): string {
    return (b ?? 'unknown').toString().toLowerCase();
  }

  severityClass(s: DriftSeverity | string | undefined | null): string {
    return (s ?? 'info').toString().toLowerCase();
  }

  statusClass(s: string | undefined | null): string {
    return (s ?? 'new').toString().toLowerCase();
  }

  priorityClass(p: string | undefined | null): string {
    return (p ?? 'normal').toString().toLowerCase();
  }

  dimSlug(t: DriftDimensionType): string { return dimSlugStatic(t); }

  slug(s: string): string { return toSlug(s); }

  formatCoverage(c: number | undefined | null): string {
    if (typeof c !== 'number' || Number.isNaN(c)) return '—';
    return `${Math.round(c * 100)}%`;
  }

  formatConfidence(c: number | undefined | null): string {
    if (typeof c !== 'number' || Number.isNaN(c)) return '—';
    return `${Math.round(c * 100)}%`;
  }

  formatTime(iso: string | undefined | null): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString('en-US');
    } catch {
      return iso;
    }
  }

  scopeLabel(r: DriftReport): string {
    const k = r.scope?.kind ?? 'Project';
    if (k === 'Task' && r.scope.taskId) return `task / ${r.scope.taskId}`;
    if (k === 'Run' && r.scope.taskId) return `run / ${r.scope.taskId}#${r.scope.runIndex ?? '?'}`;
    if (k === 'Workspace') return 'workspace';
    if (k === 'TimeWindow') return 'time-window';
    return 'project';
  }

  private describe(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }
}

/**
 * Backend serializes enum values lowercase ('schema', 'taskJob'). The
 * dimension lookup map uses our PascalCase canonical keys, so each
 * received type is normalized before insertion. Unknown keys return
 * null and the dimension is dropped from the projection.
 */
function normalizeDimensionType(raw: string | null | undefined): DriftDimensionType | null {
  if (!raw) return null;
  const lower = raw.toString().toLowerCase().replace(/[^a-z]/g, '');
  switch (lower) {
    case 'intent':       return 'Intent';
    case 'spec':         return 'Spec';
    case 'taskjob':      return 'TaskJob';
    case 'architecture': return 'Architecture';
    case 'documentation':return 'Documentation';
    case 'marketing':    return 'Marketing';
    case 'design':       return 'Design';
    case 'test':         return 'Test';
    case 'runtime':      return 'Runtime';
    case 'process':      return 'Process';
    case 'schema':       return 'Schema';
    case 'token':        return 'Token';
    default:             return null;
  }
}

function titleCase(s: string | null | undefined): string {
  if (!s) return '';
  const str = s.toString();
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
}

function dimSlugStatic(t: DriftDimensionType): string {
  // 'TaskJob' -> 'task-job'; lowercase the rest.
  switch (t) {
    case 'TaskJob': return 'task-job';
    default: return t.toLowerCase();
  }
}

function toSlug(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function truncate(s: string, max: number): string {
  if (!s) return '';
  return s.length > max ? s.slice(0, max - 1) + '…' : s;
}

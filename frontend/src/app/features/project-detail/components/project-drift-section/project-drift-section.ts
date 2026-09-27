import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, map } from 'rxjs';
import { DriftService } from '../../../../services/drift.service';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  DriftArchitectureElement,
  DriftArchitectureModel,
  DriftFindingStatus,
  DriftSeverity,
  ElementStateOverride,
} from '../../../../models/drift.model';
import { TaskState } from '../../../../models/task.model';

interface MarbleRow {
  element: DriftArchitectureElement;
  effectiveStatus: DriftFindingStatus;
  evidenceCount: number;
}

/**
 * Project-level Drift surface, architecture-marble region (ROADMAP "Drift
 * Control" - "marble-style architecture map with at most ten elements").
 * Renders the architecture model carried by the most recent drift report
 * as scan-friendly cards, each with a per-element software-drift score,
 * severity, source coverage, latest finding summary, and tracking state.
 *
 * Five surface states the spec calls out:
 *  - no architecture model (no drift report, or no model carried)
 *  - healthy map (all bands light)
 *  - warning / critical element (band-coloured marble, drilldown highlighted)
 *  - element drill-down (evidence + actions)
 *  - invalid drift JSON fallback (error pill, no marble grid)
 *
 * The map never gates pickup or transitions in V1 (taxonomy: drift scores are
 * triage signals with evidence links, not automatic decisions).
 */
@Component({
  selector: 'app-project-drift-section',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-drift-section.html',
  styleUrl: './project-drift-section.scss'
})
export class ProjectDriftSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly drift = inject(DriftService);
  private readonly jobs = inject(TaskService);

  readonly model = signal<DriftArchitectureModel | null>(null);
  readonly sourceReportId = signal<string | null>(null);
  readonly sourceReportCreatedAt = signal<string | null>(null);
  readonly overrides = signal<ElementStateOverride[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly openedId = signal<string | null>(null);
  readonly busy = signal<'analyze' | 'followup' | 'status' | null>(null);
  readonly actionMessage = signal<string | null>(null);

  readonly statuses: DriftFindingStatus[] = ['New', 'Tracked', 'Accepted', 'Ignored', 'Resolved'];

  readonly marbleRows = computed<MarbleRow[]>(() => {
    const m = this.model();
    if (!m) return [];
    const overrideMap = new Map<string, ElementStateOverride>(
      this.overrides().map(o => [o.elementId, o]),
    );
    return m.elements.map(el => ({
      element: el,
      effectiveStatus: overrideMap.get(el.elementId)?.status ?? el.status,
      evidenceCount: el.evidenceRefs.length,
    }));
  });

  readonly openedRow = computed<MarbleRow | null>(() => {
    const id = this.openedId();
    if (!id) return null;
    return this.marbleRows().find(r => r.element.elementId === id) ?? null;
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
    this.drift.getArchitecture(project).subscribe({
      next: (resp) => {
        try {
          this.model.set(resp?.model ?? null);
          this.sourceReportId.set(resp?.sourceReportId ?? null);
          this.sourceReportCreatedAt.set(resp?.sourceReportCreatedAt ?? null);
          this.overrides.set(Array.isArray(resp?.overrides) ? resp.overrides : []);
          this.error.set(null);
        } catch (e) {
          this.error.set(this.describeError(e, 'Could not project drift response.'));
        }
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describeError(err, 'Drift API call failed.'));
        this.loading.set(false);
      },
    });
  }

  toggle(elementId: string): void {
    this.openedId.update(curr => curr === elementId ? null : elementId);
    this.actionMessage.set(null);
  }

  close(): void {
    this.openedId.set(null);
    this.actionMessage.set(null);
  }

  band(severity: DriftSeverity | string): 'info' | 'warn' | 'high' | 'critical' {
    // Backend serializes enum names with the camelCase web-default policy
    // ("info" / "warn" / "high" / "critical"), but a fixture or future
    // producer may emit PascalCase. Normalize before matching so the marble
    // colour is correct in both shapes.
    switch ((severity ?? '').toString().toLowerCase()) {
      case 'critical': return 'critical';
      case 'high': return 'high';
      case 'warn': return 'warn';
      default: return 'info';
    }
  }

  /**
   * Display helper: render enum-style severity / status strings with a
   * leading capital regardless of wire case. Visible text is the contract
   * the spec asserts on; downstream comparisons (band(), markStatus()) are
   * case-insensitive on their own so the display value is the one the user
   * - and the e2e tests - read.
   */
  titleCase(value: string | null | undefined): string {
    if (!value) return '';
    const s = value.toString();
    return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  }

  formatCoverage(c: number): string {
    if (typeof c !== 'number' || Number.isNaN(c)) return '—';
    return `${Math.round(c * 100)}%`;
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

  markStatus(el: DriftArchitectureElement, status: DriftFindingStatus): void {
    const project = this.projectName();
    const m = this.model();
    if (!project || !m) return;
    this.busy.set('status');
    this.actionMessage.set(null);
    this.drift.setElementStatus(project, m.modelId, el.elementId, status).subscribe({
      next: (saved) => {
        this.overrides.update(curr => {
          const filtered = curr.filter(o => o.elementId !== saved.elementId || o.modelId !== saved.modelId);
          return [...filtered, saved];
        });
        this.busy.set(null);
        this.actionMessage.set(`Marked ${el.label} as ${status}.`);
      },
      error: (err) => {
        this.busy.set(null);
        this.actionMessage.set(this.describeError(err, 'Could not update status.'));
      },
    });
  }

  analyzeElement(el: DriftArchitectureElement): void {
    // V1: queue a follow-up task in 1-preparation that asks an agent to
    // analyze the element. Action surfaces are deliberately user-initiated;
    // the orchestrator never silently spawns a CLI from this button.
    const project = this.projectName();
    if (!project) return;
    this.busy.set('analyze');
    this.actionMessage.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busy.set(null);
          this.actionMessage.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `analyze-arch-${el.elementId}-${Date.now().toString(36)}`;
        const promptMarkdown = this.buildAnalyzePrompt(el);
        this.jobs.createJob({
          id: slug,
          title: `Analyze architecture element: ${el.label}`,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: TaskState.Preparation,
        }).subscribe({
          next: (resp) => {
            this.busy.set(null);
            this.actionMessage.set(`Queued analysis task ${resp?.id ?? slug} in 1-preparation.`);
          },
          error: (err) => {
            this.busy.set(null);
            this.actionMessage.set(this.describeError(err, 'Could not queue analysis task.'));
          },
        });
      },
      error: () => {
        this.busy.set(null);
        this.actionMessage.set('Could not resolve watch paths.');
      },
    });
  }

  createFollowUp(el: DriftArchitectureElement): void {
    const project = this.projectName();
    if (!project) return;
    this.busy.set('followup');
    this.actionMessage.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busy.set(null);
          this.actionMessage.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `followup-arch-${el.elementId}-${Date.now().toString(36)}`;
        const seedSuggestion = (el.followUpTaskSuggestions ?? [])[0];
        const title = seedSuggestion
          ? `Follow-up: ${el.label} - ${seedSuggestion}`.slice(0, 90)
          : `Follow-up on ${el.label} drift`;
        const promptMarkdown = this.buildFollowUpPrompt(el, seedSuggestion);
        this.jobs.createJob({
          id: slug,
          title,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: TaskState.Preparation,
        }).subscribe({
          next: (resp) => {
            this.busy.set(null);
            this.actionMessage.set(`Queued follow-up task ${resp?.id ?? slug} in 1-preparation.`);
          },
          error: (err) => {
            this.busy.set(null);
            this.actionMessage.set(this.describeError(err, 'Could not queue follow-up task.'));
          },
        });
      },
      error: () => {
        this.busy.set(null);
        this.actionMessage.set('Could not resolve watch paths.');
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

  private buildAnalyzePrompt(el: DriftArchitectureElement): string {
    const refs = el.evidenceRefs.map(r => `- \`${r}\``).join('\n');
    const sources = (el.sourceRefs ?? []).map(r => `- \`${r}\``).join('\n');
    return `# Analyze architecture element: ${el.label}

Spawned from the project Drift surface (architecture marble map).

**Element:** ${el.elementId}
**Expected role:** ${el.expectedRole}
**Current drift score:** ${el.score} / 100 (severity: ${el.severity})
**Source coverage:** ${(el.sourceCoverage * 100).toFixed(0)}%

## What to do

1. Inspect the element against its expected role.
2. Cross-check the listed evidence and source set for staleness or contradiction.
3. Produce a short findings note and propose follow-up tasks if drift is real.

## Evidence
${refs || '_(no evidence refs on the element)_'}

## Source set
${sources || '_(no source refs on the element)_'}
`;
  }

  private buildFollowUpPrompt(el: DriftArchitectureElement, seed: string | undefined): string {
    return `# Follow-up: ${el.label}

Created from the architecture marble drift surface.

**Element:** ${el.elementId}
**Drift score:** ${el.score} / 100 (severity: ${el.severity})
${seed ? `**Suggested action (from drift report):** ${seed}\n` : ''}
${el.summary ? `**Latest finding summary:**\n\n> ${el.summary}\n` : ''}

## What to do

Refine this scope before promoting to 2-ready: confirm the action, list affected files,
and decide whether the work belongs in a single task or several.
`;
  }

  private describeError(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }
}


import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { CLI_TYPES, CliType } from '../../../../models/task.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import type { WikiGradingRunStatus } from '../../../../models/project-docs.model';
import { CliCatalogStore } from '../../../cli';

/** Poll interval while a grading run is in flight. */
const GRADING_POLL_MS = 1200;

/**
 * The Knowledge grading maintenance run (AGT-2051): its trigger configuration,
 * its live status, and the poll that follows a run to its end. Split out of
 * `project-wiki-section.ts` in AGT-2819.
 *
 * Provided per Knowledge section, so two open sections keep their own trigger
 * configuration. The caller supplies the project on each call and is told when a
 * run finished, because refreshing the surrounding Pulse view is the section's
 * job, not this service's.
 */
@Injectable()
export class WikiGradingService implements OnDestroy {
  private readonly docs = inject(ProjectDocsService);
  private readonly catalog = inject(CliCatalogStore);

  readonly cli = signal<CliType>('claude');
  readonly model = signal<string | null>(null);
  readonly level = signal<string | null>(null);
  readonly status = signal<WikiGradingRunStatus | null>(null);

  readonly modelOptions = computed(() => this.catalog.modelsFor(this.cli()));

  private seededFor: string | null = null;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private onRunFinished: (() => void) | null = null;

  ngOnDestroy(): void {
    if (this.pollTimer) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  /** Called once a polled run leaves the running state. */
  onFinished(handler: () => void): void {
    this.onRunFinished = handler;
  }

  setModel(model: string): void {
    this.model.set(model || null);
  }

  setLevel(level: string | null): void {
    this.level.set(level);
  }

  start(project: string): void {
    if (!project) return;
    this.docs.startWikiGrading(project, {
      cliType: this.cli(),
      model: this.model() ?? undefined,
      thinkingLevel: this.level(),
    }).subscribe({
      next: status => { this.status.set(status); this.schedulePoll(project); },
      error: err => {
        // A 409 (a run already in flight) returns the live status in the body.
        const status = err?.error?.status as WikiGradingRunStatus | undefined;
        if (status) { this.status.set(status); this.schedulePoll(project); }
      },
    });
  }

  abort(project: string): void {
    if (!project) return;
    this.docs.abortWikiGrading(project).subscribe({
      next: resp => this.status.set(resp.status),
      error: () => void 0,
    });
  }

  /**
   * Loads the maintenance-model default (once per project) to pre-fill the
   * trigger's model picker, then fetches the current run status so a run started
   * in another tab is reflected and resume-polled here.
   */
  loadContext(project: string): void {
    if (!project) return;
    if (this.seededFor !== project) {
      this.seededFor = project;
      this.status.set(null);
      this.docs.getMaintenanceModel().subscribe({
        next: cfg => {
          const cli = CLI_TYPES.includes(cfg.cliType as CliType) ? (cfg.cliType as CliType) : 'claude';
          this.cli.set(cli);
          this.model.set(cfg.model || null);
          this.level.set(cfg.thinkingLevel ?? null);
        },
        error: () => void 0,
      });
    }
    this.docs.getWikiGradingStatus(project).subscribe({
      next: resp => {
        this.status.set(resp.status);
        if (resp.status?.state === 'running') this.schedulePoll(project);
      },
      error: () => void 0,
    });
  }

  /** Polls the run status while a run is in flight; notifies when it ends. */
  private schedulePoll(project: string): void {
    if (this.pollTimer) return;
    this.pollTimer = setTimeout(() => {
      this.pollTimer = null;
      if (!project) return;
      this.docs.getWikiGradingStatus(project).subscribe({
        next: resp => {
          this.status.set(resp.status);
          if (resp.status?.state === 'running') this.schedulePoll(project);
          else this.onRunFinished?.();
        },
        error: () => void 0,
      });
    }, GRADING_POLL_MS);
  }
}

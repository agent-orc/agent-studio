import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { CLI_TYPES, CliType, TaskState } from '../../../../../models/task.model';
import { DriftReport, DriftReportDetailResponse } from '../../../../../models/drift.model';
import { DriftService } from '../../../../../services/drift.service';
import { TaskService } from '../../../../../services/task.service';
import { CliCatalogStore } from '../../../../cli';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { OverlayPortalDirective } from '../../../../../directives/overlay-portal.directive';
import { copyTextToClipboard } from '../../../../../services/clipboard.util';
import {
  wikiBasename,
  wikiDescribeError,
  wikiFormatTimestamp,
  wikiSlug,
} from '../wiki-path.util';
import { type WikiLinkedElement, wikiLinkedElementKindLabel } from '../wiki-linked-element';

/** How long the copy button keeps its result wording before resetting. */
const COPY_RESET_MS = 1600;

/** Newest drift reports to load for the "Last report" facts. */
const DRIFT_REPORT_LIMIT = 12;

/**
 * Page-focused architecture drift dialog: pick a CLI and model, prepare an
 * evidence-only report, or create and start a visible CLI task.
 *
 * Split out of `project-wiki-section.ts` in AGT-2819. The section decides when
 * the dialog is open and passes the page it is about; everything the dialog does
 * with that page (prompt assembly, report loading, task creation) lives here.
 */
@Component({
  selector: 'app-wiki-drift-modal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, OverlayPortalDirective, StudioIconComponent, TooltipDirective],
  templateUrl: './wiki-drift-modal.component.html',
  styleUrl: './wiki-drift-modal.component.scss',
})
export class WikiDriftModalComponent implements OnInit, OnDestroy {
  readonly cliTypes = CLI_TYPES;
  readonly formatTimestamp = wikiFormatTimestamp;

  readonly projectName = input.required<string>();
  /** Repo-relative path of the open page; null for the root folder. */
  readonly docRel = input<string | null>(null);
  readonly docTitle = input<string>('');
  readonly docFolder = input<string>('');
  readonly docKindLabel = input<string>('');
  readonly docLinks = input<readonly WikiLinkedElement[]>([]);

  readonly closeRequest = output<void>();
  /**
   * The newest report this dialog knows about. The section's "Drift metadata"
   * meta panel reads it, so the two surfaces keep agreeing without the section
   * having to load reports itself.
   */
  readonly latestReportChange = output<DriftReport | null>();

  private readonly drift = inject(DriftService);
  private readonly tasks = inject(TaskService);
  private readonly catalog = inject(CliCatalogStore);

  readonly driftBusy = signal(false);
  readonly driftError = signal<string | null>(null);
  readonly driftMessage = signal<string | null>(null);
  readonly driftPrompt = signal('');
  readonly driftPromptLoading = signal(false);
  readonly driftReportDetail = signal<DriftReportDetailResponse | null>(null);
  readonly driftReports = signal<DriftReport[]>([]);
  readonly driftProjectKey = signal<string | null>(null);
  readonly driftWatchPath = signal<string | null>(null);
  readonly driftCli = signal<CliType>('claude');
  readonly driftModel = signal('');
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  readonly modelOptions = computed(() => this.catalog.modelsFor(this.driftCli()));

  readonly selectedModelLabel = computed(() => {
    const id = this.driftModel();
    if (!id) return 'CLI default';
    return this.modelOptions().find(m => m.id === id)?.label ?? id;
  });

  readonly latestDriftReport = computed<DriftReport | null>(() =>
    this.driftReportDetail()?.report ?? this.driftReports()[0] ?? null);

  readonly driftModalText = computed(() => {
    const markdown = this.driftReportDetail()?.markdown;
    if (markdown?.trim()) return markdown;
    return this.buildDocumentDriftPrompt();
  });

  readonly driftCopyLabel = computed(() => {
    switch (this.copyState()) {
      case 'copied': return 'Copied';
      case 'failed': return 'Copy failed';
      default: return 'Copy result';
    }
  });

  private readonly publishLatestReport = effect(() => {
    this.latestReportChange.emit(this.latestDriftReport());
  });

  /**
   * The dialog is created only while it is open, so opening it is what loads its
   * material. Required inputs are not readable in a constructor, so the load
   * runs here.
   */
  ngOnInit(): void {
    this.copyState.set('idle');
    this.catalog.ensure(this.driftCli()).subscribe({ error: () => void 0 });
    this.resolveDriftProjectContext(() => {
      this.loadDriftReports();
      this.loadDriftPrompt();
    });
  }

  ngOnDestroy(): void {
    if (this.copyResetTimer) clearTimeout(this.copyResetTimer);
  }

  onDriftCliChange(value: string): void {
    const next = CLI_TYPES.includes(value as CliType) ? value as CliType : 'claude';
    this.driftCli.set(next);
    this.driftModel.set('');
    this.catalog.ensure(next).subscribe({ error: () => void 0 });
  }

  onDriftModelChange(value: string): void {
    this.driftModel.set(value);
  }

  /** Backdrop click: a click that landed inside the card is not a dismiss. */
  requestClose(ev?: Event): void {
    if (ev && ev.target && (ev.target as HTMLElement).closest('.pwiki__drift-card')) return;
    this.closeRequest.emit();
  }

  stopModal(ev: Event): void {
    ev.stopPropagation();
  }

  prepareDriftEvidenceReport(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.driftBusy.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);
    this.drift.runSoftwareArchitectureDrift(project).subscribe({
      next: detail => {
        this.driftReportDetail.set(detail);
        this.driftReports.set([detail.report, ...this.driftReports().filter(r => r.reportId !== detail.report.reportId)]);
        this.driftBusy.set(false);
        this.driftMessage.set(`Evidence report ${detail.report.reportId} recorded.`);
      },
      error: err => {
        this.driftBusy.set(false);
        this.driftError.set(wikiDescribeError(err, 'Could not prepare the drift evidence report.'));
      },
    });
  }

  startDriftCliTask(): void {
    const project = this.projectName();
    const watchPath = this.driftWatchPath();
    if (!project || !watchPath) {
      this.resolveDriftProjectContext(() => this.startDriftCliTask());
      return;
    }

    const cli = this.driftCli();
    const model = this.driftModel().trim();
    const docSlug = wikiSlug(this.docRel() ?? 'wiki-index').slice(0, 36);
    const id = `wiki-drift-${docSlug}-${Date.now().toString(36)}`.slice(0, 96);
    this.driftBusy.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);

    this.tasks.createJob({
      id,
      title: `Knowledge drift: ${this.docTitle()}`,
      agent: cli,
      cliType: cli,
      model: model || undefined,
      watchPath,
      targetState: TaskState.Ready,
      taskType: 'chore',
      promptMarkdown: this.buildDocumentDriftPrompt(),
    }).subscribe({
      next: created => {
        const jobId = created?.id ?? id;
        this.tasks.startJob(jobId, watchPath, model || undefined, cli).subscribe({
          next: () => {
            this.driftBusy.set(false);
            this.driftMessage.set(`Started ${jobId} with ${cli}${model ? ` / ${model}` : ''}.`);
          },
          error: err => {
            this.driftBusy.set(false);
            this.driftError.set(wikiDescribeError(err, `Created ${jobId}, but could not start the CLI run.`));
          },
        });
      },
      error: err => {
        this.driftBusy.set(false);
        this.driftError.set(wikiDescribeError(err, 'Could not create the drift CLI task.'));
      },
    });
  }

  copyDriftResult(): void {
    void copyTextToClipboard(this.driftModalText()).then(ok => {
      this.copyState.set(ok ? 'copied' : 'failed');
      if (this.copyResetTimer) clearTimeout(this.copyResetTimer);
      this.copyResetTimer = setTimeout(() => {
        this.copyState.set('idle');
        this.copyResetTimer = null;
      }, COPY_RESET_MS);
    });
  }

  private resolveDriftProjectContext(after: () => void): void {
    const project = this.projectName();
    this.tasks.getWatchPaths().subscribe({
      next: entries => {
        const match = entries.find(e => e.name === project)
          ?? entries.find(e => e.path === project);
        this.driftWatchPath.set(match?.path ?? project);
        this.driftProjectKey.set(match?.path ? wikiBasename(match.path.replace(/\\/g, '/')) : project);
        after();
      },
      error: () => {
        this.driftWatchPath.set(project);
        this.driftProjectKey.set(project);
        after();
      },
    });
  }

  private loadDriftReports(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.drift.listReports(project, { limit: DRIFT_REPORT_LIMIT }).subscribe({
      next: resp => this.driftReports.set(resp?.reports ?? []),
      error: () => this.driftReports.set([]),
    });
  }

  private loadDriftPrompt(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.driftPromptLoading.set(true);
    this.drift.getSoftwareArchitectureDriftPrompt(project).subscribe({
      next: resp => {
        this.driftPrompt.set(resp.prompt ?? '');
        this.driftPromptLoading.set(false);
      },
      error: err => {
        this.driftPrompt.set('');
        this.driftPromptLoading.set(false);
        this.driftError.set(wikiDescribeError(err, 'Could not load the architecture drift prompt.'));
      },
    });
  }

  private buildDocumentDriftPrompt(): string {
    const rel = this.docRel() ?? '(root folder)';
    const title = this.docRel() ? this.docTitle() : 'Root folder';
    const report = this.latestDriftReport();
    const basePrompt = this.driftPrompt().trim();
    const linked = this.docLinks()
      .map(link => `- ${wikiLinkedElementKindLabel(link.kind)}: ${link.label} (${link.target})`)
      .join('\n');
    const model = this.driftModel().trim() || 'CLI default';

    return `# Knowledge page drift analysis: ${title}

Project: ${this.projectName()}
Page: ${rel}
Category: ${this.docRel() ? this.docFolder() : 'root folder'}
Page type: ${this.docRel() ? this.docKindLabel() : 'Root folder'}
Selected CLI: ${this.driftCli()}
Selected model: ${model}
Latest known drift report: ${report ? `${report.reportId} (${wikiFormatTimestamp(report.createdAt)})` : 'none loaded'}

## Objective

Evaluate whether the selected knowledge page still matches the current project architecture, source tree, task evidence, and concept notes.

## Linked elements visible in the Knowledge UI

${linked || '- No explicit Markdown or HTML links detected in the selected page.'}

## Required output

1. Produce a concise human-readable drift report.
2. Classify the result as Healthy, Watch, Warn, Critical, or Unknown.
3. List evidence refs using repository-relative paths.
4. Identify whether the page needs edits, a follow-up task, or no action.
5. Create or update a conceptual page-metadata note for this page. Suggested sidecar path:
   \`docs/.drift/${wikiSlug(rel)}.md\`
6. If the result should become a project drift report, post the structured response back through:
   \`POST /api/drift/{project}/actions/software-architecture-drift\`

## Base architecture-drift prompt

${basePrompt || '(Prompt not loaded yet. Use the project architecture model, docs, source tree, schemas, tests, recent tasks, and recent drift reports as evidence.)'}
`;
  }
}

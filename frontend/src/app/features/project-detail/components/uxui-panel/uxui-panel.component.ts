import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { DesignService } from '../../../../services/design.service';
import {
  DesignActionKind,
  DesignCouncilNote,
  DesignOverviewResponse,
  DesignReferenceItem,
} from './uxui-panel.types';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { designStatusTone } from './uxui-panel-status';

/**
 * Project UX/UI panel (slice 6 of the quality-system mockup,
 * docs/mockups/quality-system/). Mirrors the `ui.html` UX/UI screen:
 *
 * <list type="bullet">
 *   <item>Top metric row: design status, references count, screenshots
 *   accepted/rejected, council notes (open vs accepted).</item>
 *   <item>Design references grid: four card kinds (Markdown brief,
 *   accepted screenshots, external inspiration, rejected alternatives).</item>
 *   <item>Current design loop band: four action buttons.</item>
 *   <item>Council critique notes list with category chip and per-row
 *   Task / Accept actions. <c>parseOk = false</c> rows render the raw
 *   Markdown plus an "unstructured report" warning.</item>
 * </list>
 *
 * Action-driven principle: the panel does no analysis on its own. The
 * action buttons delegate to the backend, which queues a normal CLI job
 * for the runner to pick up; the council Accept button writes a small
 * <c>acceptedAt</c> field into the note's frontmatter and refreshes.
 */
@Component({
  selector: 'app-uxui-panel',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './uxui-panel.component.html',
  styleUrl: './uxui-panel.component.scss',
})
export class UxuiPanelComponent {
  private readonly design = inject(DesignService);

  readonly projectName = input.required<string>();

  /** Emits when the user clicks "Create follow-up task" or per-row "Task". */
  readonly createFollowUp = output<{ projectName: string; prefill: string; title: string }>();

  /** Emits when an action is successfully queued. */
  readonly actionQueuedEvent = output<{ projectName: string; action: DesignActionKind; jobId: string }>();

  readonly statusTone = designStatusTone;
  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly overview = signal<DesignOverviewResponse | null>(null);
  readonly references = signal<DesignReferenceItem[]>([]);
  readonly councilNotes = signal<DesignCouncilNote[]>([]);
  readonly busyAction = signal<DesignActionKind | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly actionQueued = signal<string | null>(null);
  readonly acceptingFile = signal<string | null>(null);
  readonly rawCache = signal<Record<string, string>>({});

  readonly acceptedRefs = computed(() => this.references().filter(r => r.kind === 'accepted'));
  readonly rejectedRefs = computed(() => this.references().filter(r => r.kind === 'rejected'));
  readonly externalRefs = computed(() => this.references().filter(r => r.kind === 'external' || (r.kind !== 'accepted' && r.kind !== 'rejected' && r.kind !== 'brief')));

  readonly totalScreenshots = computed(() => {
    const o = this.overview();
    if (!o) return 0;
    return (o.screenshotsAcceptedCount ?? 0) + (o.screenshotsRejectedCount ?? 0);
  });

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (name) this.refresh(name);
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.actionError.set(null);
    this.actionQueued.set(null);
    this.rawCache.set({});

    this.design.getOverview(name).subscribe({
      next: (o) => this.overview.set(o),
      error: (err: HttpErrorResponse) => {
        this.loadError.set(err.message ?? 'unknown');
        this.overview.set(null);
      },
    });

    this.design.listReferences(name).subscribe({
      next: (r) => this.references.set(r.references ?? []),
      error: (err: HttpErrorResponse) => {
        this.loadError.set(this.loadError() ?? err.message ?? 'unknown');
        this.references.set([]);
      },
    });

    this.design.listCouncilNotes(name).subscribe({
      next: (c) => {
        this.councilNotes.set(c.notes ?? []);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(this.loadError() ?? err.message ?? 'unknown');
        this.councilNotes.set([]);
        this.loading.set(false);
      },
    });
  }

  /** Public refresh hook so the host can re-poll after a queued action returns. */
  refreshNow(): void {
    const n = this.projectName();
    if (n) this.refresh(n);
  }

  onRunAction(action: DesignActionKind): void {
    const name = this.projectName();
    if (!name) return;
    if (this.busyAction()) return;
    this.busyAction.set(action);
    this.actionError.set(null);
    this.actionQueued.set(null);
    this.design.runAction(name, action).subscribe({
      next: (res) => {
        this.busyAction.set(null);
        this.actionQueued.set(`${actionLabel(action)} queued (${res.jobId}).`);
        this.actionQueuedEvent.emit({ projectName: name, action, jobId: res.jobId });
        // The new job appears in the kanban; refresh in case the skill has
        // already produced evidence on disk for the previous run.
        this.refresh(name);
      },
      error: (err: HttpErrorResponse) => {
        this.busyAction.set(null);
        const body = err.error;
        const reason = body?.message ?? body?.error ?? err.message ?? 'action failed';
        this.actionError.set(reason);
      },
    });
  }

  onAccept(fileName: string): void {
    const name = this.projectName();
    if (!name) return;
    if (this.acceptingFile()) return;
    this.acceptingFile.set(fileName);
    this.design.acceptCouncilNote(name, fileName).subscribe({
      next: () => {
        this.acceptingFile.set(null);
        this.refresh(name);
      },
      error: () => {
        this.acceptingFile.set(null);
      },
    });
  }

  onCreateFollowUp(reason: 'add-reference' | 'design-followup' | 'council', note?: DesignCouncilNote): void {
    const name = this.projectName();
    if (!name) return;
    let prefill: string;
    let title: string;
    if (reason === 'add-reference') {
      title = `Add design reference (${name})`;
      prefill = [
        '# Add design reference',
        '',
        'Add a screenshot, brief, or external reference under `design/references/` for this project.',
        'Frontmatter must include `kind: accepted|rejected|external` and an optional `screenshot:` path.',
      ].join('\n');
    } else if (reason === 'council' && note) {
      title = `Design follow-up: ${note.title ?? note.fileName}`;
      const lines = ['# Design follow-up', ''];
      lines.push(`Source council note: \`${note.relPath}\`${note.noteDate ? ' (' + note.noteDate + ')' : ''}.`);
      if (note.category) lines.push(`Category: ${note.category}`);
      if (note.summary) {
        lines.push('');
        lines.push(note.summary);
      }
      lines.push('');
      lines.push('Action: address the council finding above. Evidence-driven; do not silently mutate task state.');
      prefill = lines.join('\n');
    } else {
      title = `Design follow-up (${name})`;
      const o = this.overview();
      const lines = ['# Design follow-up', ''];
      if (o?.briefSummary) {
        lines.push(o.briefSummary);
        lines.push('');
      }
      lines.push(`Open council notes: ${o?.councilOpenCount ?? 0}.`);
      lines.push('');
      lines.push('Action: pick the most relevant open council note and address it as a normal queued task.');
      prefill = lines.join('\n');
    }
    this.createFollowUp.emit({ projectName: name, prefill, title });
  }

  loadRaw(fileName: string): void {
    const name = this.projectName();
    if (!name) return;
    if (this.rawCache()[fileName] !== undefined) return;
    this.design.readCouncilNote(name, fileName).subscribe({
      next: (res) => {
        const next = { ...this.rawCache(), [fileName]: res.content };
        this.rawCache.set(next);
      },
      error: (err: HttpErrorResponse) => {
        const next = { ...this.rawCache(), [fileName]: `Failed to load: ${err.message ?? 'unknown'}` };
        this.rawCache.set(next);
      },
    });
  }

  categoryTone(category: string | null | undefined): string {
    if (!category) return 'neutral';
    const c = category.toLowerCase();
    if (c.includes('workflow')) return 'workflow';
    if (c.includes('polish')) return 'polish';
    if (c.includes('a11y') || c.includes('access')) return 'a11y';
    if (c.includes('product')) return 'product';
    if (c.includes('visual')) return 'visual';
    if (c.includes('interaction')) return 'interaction';
    return 'neutral';
  }
}

function actionLabel(action: DesignActionKind): string {
  switch (action) {
    case 'screenshot-critique': return 'Screenshot critique';
    case 'council-review': return 'Council review';
    case 'request-next-version': return 'Next-version plan';
    default: return action;
  }
}

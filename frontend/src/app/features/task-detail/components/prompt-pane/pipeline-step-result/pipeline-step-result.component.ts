import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { TaskService } from '../../../../../services/task.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { FileSourceHistoryComponent } from '../../../../../components/file-source-history/file-source-history.component';
import { cleanStepResultMarkdown } from './pipeline-step-result.util';

/**
 * Compact header for a step-result card: the step name plus its outcome and
 * run metadata (model / duration / tokens / cost). Built by the Overview pane
 * from a pipeline row so the card has a self-contained "schöner HTML-Header"
 * even though the row above it carries the same summary.
 */
export interface PipelineStepResultHeader {
  label: string;
  statusIcon: string;
  statusLabel: string;
  /** Raw status token (`passed` | `failed` | …) for the tone data-attribute. */
  status: string;
  verdict: string | null;
  model: string | null;
  durationLabel: string | null;
  tokensLabel: string | null;
  costLabel: string | null;
}

/**
 * Popover, well-formatted result view for one pipeline step (Per-step
 * structured result). Renders beside its Overview pipeline row trigger as a
 * card with a header (title + status + verdict + model / timing / token / cost
 * meta) and a body that lazily fetches the
 * step's on-disk markdown (`status.md` for the CORE run, `aspect-{id}.md` for
 * a review aspect) and renders it through the shared {@link
 * FileSourceHistoryComponent} so the same prose-plus-git-timeline mechanic that
 * the Files and Code-Review panes use also backs each pipeline step. Cleaning
 * ({@link cleanStepResultMarkdown}) is passed as the history `contentTransform`
 * so the live body and every historical version strip the frontmatter, unwrap
 * the model-reply fence, and drop machine sentinels — the operator reads
 * formatted prose, not a raw blob, at any point on the timeline.
 *
 * Self-contained: a closed popover costs nothing; the file is fetched on the
 * first open and cached for the component's lifetime.
 */
@Component({
  selector: 'app-pipeline-step-result',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FileSourceHistoryComponent, TooltipDirective],
  templateUrl: './pipeline-step-result.component.html',
  styleUrl: './pipeline-step-result.component.scss',
})
export class PipelineStepResultComponent {
  readonly header = input.required<PipelineStepResultHeader>();
  readonly jobId = input.required<string>();
  readonly watchPath = input<string>();
  /** Job-root markdown file to render (e.g. `status.md`, `aspect-code-quality.md`). */
  readonly fileName = input.required<string>();

  private readonly tasks = inject(TaskService);

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly failed = signal(false);
  /** Raw on-disk body. Cleaning runs in the history `contentTransform`, not here. */
  readonly content = signal<string>('');

  /** Stable transform reference handed to the history pane (live + historical bodies). */
  readonly cleanTransform = (raw: string): string => cleanStepResultMarkdown(raw);

  readonly hasContent = computed(() => this.cleanTransform(this.content()).length > 0);

  toggle(): void {
    const next = !this.open();
    this.open.set(next);
    if (next && !this.loaded() && !this.loading()) this.load();
  }

  close(): void {
    this.open.set(false);
  }

  private load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.tasks.readJobFile(this.jobId(), this.fileName(), this.watchPath()).subscribe({
      next: (text) => {
        this.content.set(typeof text === 'string' ? text : '');
        this.loaded.set(true);
        this.loading.set(false);
      },
      error: () => {
        this.failed.set(true);
        this.loaded.set(true);
        this.loading.set(false);
      },
    });
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { OverlayPortalDirective } from '@studio/ui/directives/overlay-portal.directive';
import { ModalStackService } from '@studio/ui/services/modal-stack.service';
import { resolveProtocolImageSrc } from '../../protocol-pane/protocol-image-resolver';

/**
 * "Prompt" affordance for the Overview tab. A small trigger next to the task
 * title opens a centered, read-only modal that renders the task prompt
 * (`prompt.md` / `promptMarkdown`) as Markdown via the shared
 * {@link MarkdownViewComponent}. The Overview tab summarises status, agent,
 * tokens and pipeline but never the prompt itself; this is the focused way to
 * read what the task was actually asked to do without leaving for the Files tab.
 *
 * The modal is portaled to the central modal overlay layer so it stays above
 * sibling panes and out of ancestor overflow clipping. Closes on backdrop click,
 * close button, and Escape routed through {@link ModalStackService} so another
 * modal above wins first and Escape does not close the detail panel behind it.
 * The body is scrollable for long prompts. The trigger hides itself when there
 * is no prompt text so it never opens an empty modal.
 */
@Component({
  selector: 'app-task-prompt-popover',
  standalone: true,
  imports: [MarkdownViewComponent, OverlayPortalDirective, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-prompt-popover.component.html',
  styleUrl: './task-prompt-popover.component.scss',
})
export class TaskPromptPopoverComponent {
  /** Raw prompt markdown (`promptMarkdown` of the task). */
  readonly markdown = input<string | null | undefined>('');
  /** Job id + watch path, used to resolve `attachments/...` image refs. */
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  /** Modal heading + dialog aria-label. Defaults to the task-prompt wording;
   *  the per-step reuse passes "Step prompt". */
  readonly title = input<string>('Task prompt');
  /** Trigger button caption. */
  readonly triggerLabel = input<string>('Prompt');
  /** Trigger hover tooltip. */
  readonly triggerTooltip = input<string>('Show the task prompt (prompt.md) rendered as Markdown');
  /** Trigger test id, made unique per step when reused in the pipeline rows. */
  readonly triggerTestid = input<string>('overview-prompt-trigger');
  /** Modal-stack key so Escape arbitration can tell instances apart. */
  readonly modalStackId = input<string>('task-prompt-popover');

  readonly open = signal(false);

  /** True only when there is non-empty prompt text to show. */
  readonly hasPrompt = computed(() => (this.markdown() ?? '').trim().length > 0);

  /** Rewrites `attachments/foo.png` refs to the job-folder API URL so prompt
   *  images render inside the popover the same way they do on the Files tab. */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return (src: string) => resolveProtocolImageSrc(src, jobId, watchPath);
  });

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

  toggle(event: Event): void {
    event.stopPropagation();
    if (!this.hasPrompt()) return;
    this.open.update(v => !v);
  }

  close(): void {
    this.open.set(false);
  }

  // Escape routes through ModalStack so a real modal above wins first and the
  // detail panel does not close behind us.
  private readonly stackEffect = effect(() => {
    const isOpen = this.open();
    if (isOpen && !this.modalStackDispose) {
      this.modalStackDispose = this.modalStack.push(this.modalStackId(), () => {
        this.close();
        return true;
      });
    } else if (!isOpen && this.modalStackDispose) {
      this.modalStackDispose();
      this.modalStackDispose = null;
    }
  });
  private readonly stackTeardown = this.destroyRef.onDestroy(() => {
    this.modalStackDispose?.();
  });
}

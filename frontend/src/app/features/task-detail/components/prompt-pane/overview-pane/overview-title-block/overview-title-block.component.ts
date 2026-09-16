import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, ViewChild, computed, effect, inject, input, output, signal,
} from '@angular/core';
import { TaskState, type PromoteToCodingResponse, type TaskInfo } from '../../../../../../models/task.model';
import { CreateTaskFormService, type PendingAttachment } from '../../../../../board';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ConceptDossierNoticeComponent } from '../../../concept-dossier-notice/concept-dossier-notice.component';
import { PlanningSpawnPanelComponent } from '../../../planning-spawn-panel/planning-spawn-panel.component';
import { DeliveryClaimPanelComponent } from '../../../delivery-claim-panel/delivery-claim-panel.component';
import { CopyableTaskKeyComponent } from '../../../../../../components/copyable-task-key/copyable-task-key.component';
import { ExecutionLocationBadgeComponent } from '../../../../../../components/execution-location-badge/execution-location-badge.component';
import { TaskPromptPopoverComponent } from '../../task-prompt-popover/task-prompt-popover.component';
import { laneTone } from '../../../../../../models/lane-presentation';
import { copyTextToClipboard } from '../../../../../../services/clipboard.util';
import { lifecyclePhaseLabel } from '../../../../../../services/lifecycle-phase.util';
import { NotificationService } from '../../../../../../services/notification.service';
import { ModalStackService } from '../../../../../../services/modal-stack.service';
import { projectIdentity } from '../../../../../../services/project-identity.util';
import { TaskService } from '../../../../../../services/task.service';
import { laneLabel } from '../overview-pane-formatters';

/** Short unique id for a seeded create-modal attachment (mirrors the dialog's own). */
function makeAttachmentId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
  }
  return Math.random().toString(36).slice(2, 14);
}

/**
 * The Overview tab's hero block: the inline-editable title, the identity
 * sub-line (key, lane, phase, project, execution location, prompt popover), and
 * the promote-to-coding affordance for a finished planning task.
 *
 * Split out of `overview-pane.component.ts` in AGT-2819. It owns one coherent
 * job: naming the card and acting on that naming. Nothing here reads pipeline,
 * token, or run state, which is exactly why it separates cleanly.
 */
@Component({
  selector: 'app-overview-title-block',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    TooltipDirective,
    ConceptDossierNoticeComponent,
    PlanningSpawnPanelComponent,
    DeliveryClaimPanelComponent,
    CopyableTaskKeyComponent,
    ExecutionLocationBadgeComponent,
    TaskPromptPopoverComponent,
  ],
  templateUrl: './overview-title-block.component.html',
  styleUrl: './overview-title-block.component.scss',
})
export class OverviewTitleBlockComponent {
  readonly laneLabel = laneLabel;
  readonly laneTone = laneTone;

  readonly job = input.required<TaskInfo>();
  /** Raw task prompt markdown, surfaced through the sub-line's popover. */
  readonly promptMarkdown = input<string | null | undefined>('');
  /**
   * The pane's live tick. Passed in rather than owned here so the phase chip's
   * elapsed wording advances on the same clock as the pipeline step timings
   * instead of on a second, unsynchronised interval.
   */
  readonly nowMs = input<number>(Date.now());

  /** Fired after a successful title PUT so the parent can re-fetch the detail. */
  readonly titleSaved = output<void>();
  readonly referencesChanged = output<void>();

  private readonly jobService = inject(TaskService);
  private readonly notifs = inject(NotificationService);
  private readonly modalStack = inject(ModalStackService);
  private readonly createForm = inject(CreateTaskFormService);
  private readonly destroyRef = inject(DestroyRef);

  /** Title inline-edit state. Optimistic: `optimisticTitle` overrides the
   *  displayed value the moment the user hits Enter / blurs, so there is no
   *  spinner between PUT and the parent's re-fetch landing. */
  readonly editingTitle = signal(false);
  readonly titleDraft = signal('');
  readonly savingTitle = signal(false);
  private readonly optimisticTitle = signal<string | null>(null);
  private modalStackDisposer: (() => void) | null = null;

  /** Title the H1 renders. Falls back to the job id when no title is set. */
  readonly displayedTitle = computed<string>(() => {
    const opt = this.optimisticTitle();
    if (opt != null) return opt;
    return this.job().title || this.job().id;
  });

  /** Visual identity (initial + colour) of the project, for the sub-line. */
  readonly identity = computed(() => projectIdentity(this.job().projectName));

  /** In-flight guard for the promote-to-coding fetch (payload + images). */
  readonly promoting = signal(false);

  /**
   * Lanes a planning task counts as "finished successfully" for the promote
   * affordance - it has reached review or completion, not a failure / still
   * running lane. See docs/concepts/planning-research-task-kinds-2026-05.md.
   */
  private static readonly FINISHED_STATES = new Set<string>([
    TaskState.AutoReview,
    TaskState.HumanReview,
    TaskState.Escalated,
    TaskState.Completed,
  ]);

  /**
   * "Promote to coding task" is offered only on a planning task whose latest
   * run finished. Research tasks are read-only reports by design and never show
   * it; coding tasks have nothing to promote to.
   */
  readonly canPromote = computed(() =>
    this.job().mode === 'planning'
    && OverviewTitleBlockComponent.FINISHED_STATES.has(this.job().state),
  );

  /**
   * Fetch the pre-fill draft for this planning task, pull each copyable image
   * down as a blob (so the create modal can re-upload it byte-for-byte), then
   * open the create-task modal seeded with that draft. The modal stays the
   * single source of truth for the create UX.
   */
  promote(): void {
    if (this.promoting() || !this.canPromote()) return;
    const job = this.job();
    this.promoting.set(true);
    this.jobService.getPromoteToCoding(job.id, job.watchPath).subscribe({
      next: (payload) => {
        void this.fetchPromoteAttachments(payload).then((attachments) => {
          this.createForm.openPromotePlanning(payload, attachments);
          this.promoting.set(false);
        });
      },
      error: () => {
        this.promoting.set(false);
        this.notifs.warning(
          'Could not prepare a coding task from this planning report. Try again in a moment.',
          'Promote failed',
        );
      },
    });
  }

  /**
   * Download each promote attachment as a File wrapped in a PendingAttachment.
   * A single failed image is skipped (the rest still come along) rather than
   * failing the whole promotion.
   */
  private async fetchPromoteAttachments(payload: PromoteToCodingResponse): Promise<PendingAttachment[]> {
    const pending: PendingAttachment[] = [];
    for (const ref of payload.attachments) {
      try {
        const res = await fetch(ref.url);
        if (!res.ok) continue;
        const blob = await res.blob();
        const file = new File([blob], ref.fileName, { type: blob.type || 'image/png' });
        pending.push({
          id: makeAttachmentId(),
          file,
          alt: ref.fileName,
          previewUrl: URL.createObjectURL(blob),
        });
      } catch {
        // Skip this image; keep the rest of the promotion intact.
      }
    }
    return pending;
  }

  /** Clear the optimistic override once the real `job().title` catches up to
   *  the saved value (parent re-fetched the detail after PUT). */
  private clearOptimisticOnSync = effect(() => {
    const opt = this.optimisticTitle();
    if (opt == null) return;
    const current = this.job().title || this.job().id;
    if (current === opt) {
      this.optimisticTitle.set(null);
    }
  });

  @ViewChild('titleInput') private titleInputEl?: ElementRef<HTMLInputElement>;

  private focusOnEdit = effect(() => {
    if (this.editingTitle()) {
      queueMicrotask(() => this.titleInputEl?.nativeElement.select());
    }
  });

  startTitleEdit(): void {
    if (this.editingTitle()) return;
    this.titleDraft.set(this.displayedTitle());
    this.editingTitle.set(true);
    // Push a modal-stack entry so Escape closes the edit, not the detail
    // panel. The detail view's own modal-stack entry only checks its
    // editingTitle / editingPrompt signals (set by the detail-header), so
    // without this Escape would bubble past our local cancel and close the
    // whole detail view.
    this.modalStackDisposer = this.modalStack.push('overview-title-edit', () => {
      this.cancelTitleEdit();
      return true;
    });
    this.destroyRef.onDestroy(() => this.disposeModalStack());
  }

  cancelTitleEdit(): void {
    this.editingTitle.set(false);
    this.savingTitle.set(false);
    this.disposeModalStack();
  }

  saveTitle(): void {
    if (!this.editingTitle()) return;
    const trimmed = this.titleDraft().trim();
    if (!trimmed) {
      this.cancelTitleEdit();
      return;
    }
    const current = this.displayedTitle();
    if (trimmed === current) {
      this.cancelTitleEdit();
      return;
    }
    // Optimistic: paint the new title immediately, drop edit mode, fire the
    // PUT without a spinner. Revert on error.
    const job = this.job();
    this.optimisticTitle.set(trimmed);
    this.editingTitle.set(false);
    this.savingTitle.set(false);
    this.disposeModalStack();
    this.jobService.setJobTitle(job.id, trimmed, job.watchPath).subscribe({
      next: () => {
        this.titleSaved.emit();
      },
      error: () => {
        this.optimisticTitle.set(null);
        this.notifs.warning(
          'The new title could not be saved. The previous title was restored.',
          'Title save failed',
        );
      },
    });
  }

  copyTitle(): void {
    const text = this.displayedTitle();
    if (!text) return;
    copyTextToClipboard(text).then(ok => {
      if (ok) this.notifs.success('Task title copied to clipboard', 'Title copied');
    });
  }

  onTitleDraftInput(value: string): void {
    this.titleDraft.set(value);
  }

  phaseLabel(phase: string | null | undefined, entered?: string | null, steerSince?: string | null): string | null {
    return lifecyclePhaseLabel(phase, entered, steerSince, this.nowMs());
  }

  private disposeModalStack(): void {
    if (this.modalStackDisposer) {
      this.modalStackDisposer();
      this.modalStackDisposer = null;
    }
  }
}

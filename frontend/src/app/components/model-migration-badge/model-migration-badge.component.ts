import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  OnDestroy,
  ViewChild,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { PendingButtonDirective } from '../async-feedback';
import { NotificationService } from '../../services/notification.service';
import { TaskService } from '../../services/task.service';
import {
  ModelMigrationCatalogStore,
  type ModelMigrationEntry,
} from '../../features/cli/model-migrations';
import { OverlayPortalRef, OverlayPortalService, type ConnectedOverlayPositionRef } from '../../services/overlay-portal.service';

/**
 * AGT-2716 — the "model update available" affordance for a surface pinned to
 * an explicit model the {@link ModelMigrationCatalogStore} has since
 * superseded (e.g. `claude-opus-4-8` when `claude-opus-5` is current).
 *
 * Renders nothing when the catalog has no proposal for `model()` (or when
 * `explicit()` is false). Otherwise shows a small dot next to the caller's
 * own model badge; clicking it opens a compact popover with the catalog's
 * `from -> to` and `reason`, plus an Apply button.
 *
 * Two apply modes so one component serves every consumer (AGT-2716 scope:
 * card pins, task-detail header, project pipeline-step overrides, CLI
 * Management primary-model routes):
 *  - **Task-pin mode** (`jobId` set): applies via `TaskService.setJobModel`
 *    directly and owns its own pending state + success/error toast, mirroring
 *    `IntegrationStatusBadgeComponent`'s self-contained recovery action. The
 *    board card and task-detail header just point it at the job.
 *  - **Delegated mode** (`jobId` unset): emits `(apply)` with the target
 *    model id and expects the caller to own the pending state (via the
 *    `pending` input) and perform the actual write. Pipeline-step overrides
 *    and CLI route commits each have their own field-preservation logic
 *    (`onStepAgentCommit` / `setPrimary`) that this component must not
 *    bypass by hand-rolling a competing PUT.
 *
 * The popover is portaled to the shared body-level overlay layer
 * (`OverlayPortalService`) so it is never clipped by an ancestor's
 * `overflow: hidden` / `content-visibility` containment (the board card sets
 * both) — the same technique `<app-menu>` and the token-usage popover use.
 */
@Component({
  selector: 'app-model-migration-badge',
  standalone: true,
  imports: [TooltipDirective, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './model-migration-badge.component.html',
  styleUrl: './model-migration-badge.component.scss',
})
export class ModelMigrationBadgeComponent implements OnDestroy {
  private readonly migrations = inject(ModelMigrationCatalogStore);
  private readonly notifications = inject(NotificationService);
  private readonly tasks = inject(TaskService);
  private readonly overlayPortal = inject(OverlayPortalService);

  /** The current explicitly-pinned model id to check against the catalog. */
  readonly model = input<string | null>(null);
  /** Gate: only an explicit pin is eligible for an offer (default true for
   *  surfaces that always resolve to an explicit override, e.g. pipeline steps). */
  readonly explicit = input(true);
  /** Task-pin mode: when set, Apply calls `TaskService.setJobModel` directly. */
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  /** Delegated mode: external pending flag while the caller's own PUT is in flight. */
  readonly pending = input(false);
  readonly testId = input('model-migration-badge');

  /** Delegated mode: emits the migration's target model id on Apply. */
  readonly apply = output<string>();

  @ViewChild('trigger') private triggerRef?: ElementRef<HTMLButtonElement>;
  @ViewChild('panel') private panelRef?: ElementRef<HTMLDivElement>;

  private readonly selfPending = signal(false);
  private portalRef: OverlayPortalRef | null = null;
  private positionRef: ConnectedOverlayPositionRef | null = null;

  readonly proposal = computed<ModelMigrationEntry | null>(() =>
    this.explicit() ? this.migrations.proposalFor(this.model()) : null);

  readonly isPending = computed(() => (this.jobId() ? this.selfPending() : this.pending()));

  readonly open = signal(false);

  ngOnDestroy(): void {
    this.teardownPortal();
  }

  /**
   * Closes the popover on any click outside it. The panel is portaled to
   * `<body>`, but its own `onPanelClick` / the toggle's `toggle` both call
   * `stopPropagation`, so only genuine outside clicks ever reach `document`.
   */
  @HostListener('document:click')
  onDocumentClick(): void {
    if (this.open()) this.close();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.close();
  }

  toggle(event: Event): void {
    event.stopPropagation();
    if (!this.proposal()) return;
    this.open.update((v) => !v);
    if (this.open()) {
      queueMicrotask(() => this.attachPortal());
    } else {
      this.teardownPortal();
    }
  }

  onPanelClick(event: Event): void {
    event.stopPropagation();
  }

  close(): void {
    this.open.set(false);
    this.teardownPortal();
  }

  onApply(event: Event): void {
    event.stopPropagation();
    const proposal = this.proposal();
    if (!proposal || this.isPending()) return;
    const jobId = this.jobId();
    if (!jobId) {
      this.apply.emit(proposal.to);
      this.close();
      return;
    }
    this.selfPending.set(true);
    this.tasks.setJobModel(jobId, proposal.to, this.watchPath() ?? undefined).subscribe({
      next: () => {
        this.selfPending.set(false);
        this.close();
        this.notifications.success(`Model updated to ${proposal.to}.`);
      },
      error: () => {
        this.selfPending.set(false);
        this.notifications.error('Could not apply the model migration.');
      },
    });
  }

  private attachPortal(): void {
    const panel = this.panelRef?.nativeElement;
    const trigger = this.triggerRef?.nativeElement;
    if (!panel || !trigger || !this.open()) return;
    this.teardownPortal();
    this.portalRef = this.overlayPortal.attachPanel(panel);
    this.positionRef = this.overlayPortal.watchConnectedPosition(trigger, panel, {
      preferredPlacement: 'below',
      alignment: 'start',
      gap: 6,
      viewportPadding: 8,
      minWidth: 220,
    });
  }

  private teardownPortal(): void {
    this.positionRef?.dispose();
    this.positionRef = null;
    this.portalRef?.dispose();
    this.portalRef = null;
  }
}

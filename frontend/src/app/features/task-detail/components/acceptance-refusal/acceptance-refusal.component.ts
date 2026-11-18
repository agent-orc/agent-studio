import {
  ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, computed, effect, inject, input, signal, untracked, viewChild,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { ConnectedOverlayDirective } from '../../../../directives/connected-overlay.directive';
import type { TaskInfo } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { serverErrorMessage } from '../../../../utils/server-error-message';
import {
  AcceptanceRefusalStore,
  type AcceptanceRefusal,
} from '../../state/acceptance-refusal.model';
import { AcceptanceReadinessStore } from '../../../acceptance-readiness';
import { AcceptanceRoundDialogComponent } from '../../../acceptance-readiness/lazy';

/** A refusal request for focus is honoured only this long after the refusal. */
const FOCUS_WINDOW_MS = 10_000;
const FOCUS_ATTEMPTS_MS = [0, 150, 500];

/** Attribute of the task-detail pane surface that focuses itself on mount. */
const AUTO_FOCUS_SURFACE = 'appdetailkeyboardsurface';

/** False inside a `display: none` twin; true where the check is unavailable. */
function isVisible(el: HTMLElement): boolean {
  return typeof el.checkVisibility === 'function' ? el.checkVisibility() : true;
}

/**
 * AGT-3704: the reason a Human Review Accept is held or was refused, rendered
 * in the task toolbar directly before the Accept button, with the one action
 * that resolves it. The owner override asks for a written reason, which the
 * server records on the card (`operatorOverride` on the move to Accepted).
 *
 * AGT-3830: a content refusal (`replacesAccept`) renders "Review again" as the
 * primary and "Accept with a reason…" beside it, in place of Accept. The
 * `stacked` layout is the phone's full-width form of the same actions. After
 * a refused click the reason takes focus, so the outcome is read out where
 * the next step is.
 */
@Component({
  selector: 'app-acceptance-refusal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent, ConnectedOverlayDirective, TooltipDirective, AcceptanceRoundDialogComponent],
  templateUrl: './acceptance-refusal.component.html',
  styleUrl: './acceptance-refusal.component.scss',
})
export class AcceptanceRefusalComponent {
  readonly refusal = input.required<AcceptanceRefusal>();
  readonly job = input.required<TaskInfo>();
  readonly disabled = input(false);
  /** `inline`: a toolbar item. `stacked`: the phone's full-width block. */
  readonly layout = input<'inline' | 'stacked'>('inline');
  private readonly reasonEl = viewChild<ElementRef<HTMLElement>>('reasonEl');
  private readonly actionButton = viewChild<ElementRef<HTMLButtonElement>>('actionButton');
  private readonly secondaryButton = viewChild<ElementRef<HTMLButtonElement>>('secondaryButton');
  private readonly reasonInput = viewChild<ElementRef<HTMLTextAreaElement>>('reasonInput');
  private readonly focusOverrideInput = signal(false);
  private readonly injector = inject(Injector);

  readonly overrideOpen = signal(false);
  readonly overrideAnchor = signal<HTMLElement | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** The ranked readiness reason and other gaps, also exposed to assistive technology. */
  readonly detail = computed(() => {
    const refusal = this.refusal();
    return [
      refusal.headline ? `${refusal.headline}: ${refusal.reason}` : refusal.reason,
      refusal.nextStep ? `Next step: ${refusal.nextStep}` : null,
      ...(refusal.secondary ?? []).map(gap => `Also: ${gap}`),
    ].filter(Boolean).join(' ');
  });
  /** The action's button classes: the toolbar's vocabulary inline, the phone's own when stacked. */
  readonly actionClass = computed(() => {
    const primary = !!this.refusal().replacesAccept;
    return this.layout() === 'stacked'
      ? `acceptance-refusal__button${primary ? ' acceptance-refusal__button--primary' : ''}`
      : `studio-tab-action ${primary ? 'studio-tab-action--primary' : 'studio-tab-action--toggle'}`;
  });
  readonly secondaryClass = computed(() => this.layout() === 'stacked'
    ? 'acceptance-refusal__button' : 'studio-tab-action studio-tab-action--toggle');
  readonly actionTooltip = computed(() => ({
    'override': 'Accept now and record a written reason on the card',
    'review-again': 'Run the content review again against the current brief',
    'retry-integration': 'Run the integration into the target branch again',
    'send-round': 'Review and send the prefilled round that closes the gap',
  })[this.refusal().action]);
  readonly secondaryTooltip = computed(() => this.refusal().secondaryAction?.action === 'send-round'
    ? 'Review and send the prefilled round that closes the gap'
    : 'Accept now and record a written reason on the card');
  readonly canSubmit = computed(() => !this.busy() && this.reason().trim().length >= 8);

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly refusals = inject(AcceptanceRefusalStore);
  private readonly readiness = inject(AcceptanceReadinessStore);

  /** AGT-3867: the round this toolbar opened, when an override cannot admit the card. */
  readonly roundDraft = computed(() => {
    const draft = this.readiness.draftFor('toolbar');
    return draft && draft.job.id === this.job().id ? draft : null;
  });

  private readonly focusReason = effect(() => {
    const request = this.refusals.focusRequest();
    const taskKey = this.job().taskKey;
    if (!request || request.taskKey !== taskKey) return;
    // After the render and after the restored detail's own mount focus (its
    // keyboard surface focuses itself in a microtask), which this replaces.
    // A detail that paints late gets a second and third try.
    afterNextRender(() => {
      for (const delay of FOCUS_ATTEMPTS_MS) setTimeout(() => this.takeFocus(request.at), delay);
    }, { injector: this.injector });
  });

  private readonly openOnRequest = effect(() => {
    const requested = this.refusals.overrideRequest();
    const refusal = this.refusal();
    const button = (refusal.action === 'override' ? this.actionButton() :
      refusal.secondaryAction?.action === 'override' ? this.secondaryButton() : null)?.nativeElement;
    if (!requested || requested !== this.job().taskKey || !button) return;
    untracked(() => {
      if (!this.refusals.takeOverrideRequest(requested) || this.disabled()) return;
      this.error.set(null);
      this.overrideAnchor.set(button);
      this.overrideOpen.set(true);
      this.focusOverrideInput.set(true);
    });
  });

  private readonly focusOnOverrideOpen = effect(() => {
    const input = this.reasonInput()?.nativeElement;
    if (!input || !this.focusOverrideInput()) return;
    untracked(() => {
      this.focusOverrideInput.set(false);
      input.focus({ preventScroll: true });
    });
  });

  private takeFocus(requestedAt: number): void {
    const el = this.reasonEl()?.nativeElement;
    // The toolbar and the phone each render this refusal; only the visible one takes focus.
    if (!el || !isVisible(el) || Date.now() - requestedAt > FOCUS_WINDOW_MS) return;
    // Take focus only from the body (the refused Accept left the DOM) or from
    // a surface that focused itself on mount, never from a user's control.
    const active = document.activeElement;
    if (active && active !== document.body && active !== el && !active.hasAttribute(AUTO_FOCUS_SURFACE)) return;
    el.focus();
  }

  onAction(event: MouseEvent): void {
    if (this.disabled() || this.busy()) return;
    if (this.refusal().action === 'retry-integration') {
      this.retryIntegration();
      return;
    }
    if (this.refusal().action === 'review-again') {
      this.reviewAgain();
      return;
    }
    if (this.refusal().action === 'send-round') {
      this.readiness.openRound(this.job(), 'toolbar');
      return;
    }
    this.toggleOverride(event);
  }

  onSecondary(event: MouseEvent): void {
    if (this.disabled() || this.busy()) return;
    if (this.refusal().secondaryAction?.action === 'send-round') {
      this.readiness.openRound(this.job(), 'toolbar');
      return;
    }
    this.toggleOverride(event);
  }

  toggleOverride(event: MouseEvent): void {
    if (this.disabled() || this.busy()) return;
    this.error.set(null);
    this.overrideAnchor.set(event.currentTarget as HTMLElement);
    this.overrideOpen.update(open => !open);
  }

  onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
  }

  cancelOverride(): void {
    if (this.busy()) return;
    this.overrideOpen.set(false);
    this.reason.set('');
    this.error.set(null);
  }

  acceptWithOverride(): void {
    if (!this.canSubmit()) return;
    const job = this.job();
    this.busy.set(true);
    this.error.set(null);
    this.tasks.moveJob(job.id, '6-completed', job.watchPath ?? undefined, undefined,
      this.reason().trim(), true).subscribe({
      next: () => {
        this.busy.set(false);
        this.overrideOpen.set(false);
        this.reason.set('');
        this.refusals.clear(job.taskKey);
        this.notifications.success('Accepted by owner override. The written reason is recorded on the card.');
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(serverErrorMessage(err, 'The override was not accepted.'));
      },
    });
  }

  private reviewAgain(): void {
    const job = this.job();
    this.busy.set(true);
    this.error.set(null);
    this.tasks.reviewContentAgainstCurrentBrief(job.id, job.watchPath ?? undefined).subscribe({
      next: () => {
        this.busy.set(false);
        this.refusals.clear(job.taskKey);
        this.notifications.success('Content review requested for the current brief.');
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(serverErrorMessage(err, 'Could not request content review.'));
      },
    });
  }

  private retryIntegration(): void {
    const job = this.job();
    this.busy.set(true);
    this.tasks.retryIntegration(job.id, job.watchPath ?? undefined).subscribe({
      next: () => {
        this.busy.set(false);
        this.refusals.clear(job.taskKey);
        this.notifications.success('Integration retry queued.');
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.notifications.error(serverErrorMessage(err, 'Could not retry integration.'));
      },
    });
  }
}

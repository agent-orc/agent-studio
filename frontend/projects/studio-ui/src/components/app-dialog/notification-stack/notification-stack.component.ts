import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  afterEveryRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { NotificationService } from '../../../services/notification.service';
import { NotificationComponent } from '../../notification/notification.component';
import { NotificationAction, NotificationState } from '../../../models/app-dialog.model';

/**
 * Stack of persistent notification toasts (success / info / warning / error).
 * Mounted once at the app shell; visibility driven by
 * `NotificationService.notifications()`.
 *
 * F56: all notifications render as toasts in a top-right stack. Click-to-dismiss
 * is the default; auto-dismiss only for short success/info without actions.
 * Escape dismisses the topmost toast (when no modal has consumed the event).
 *
 * Toasts are routed into two positioned containers by their `position`
 * field: the default `top-right` pile and a `bottom-right` pile reserved
 * for action toasts (Move/Undo) that must not occlude the top-right
 * context menu. A third pile docks `below-toolbar` toasts under the task
 * tab toolbar and the card's refusal banner (AGT-3873), so a refusal toast
 * never covers the toolbar action it names.
 */
@Component({
  selector: 'sui-notification-stack, app-notification-stack',
  standalone: true,
  imports: [NotificationComponent, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './notification-stack.component.html',
})
export class NotificationStackComponent {
  private readonly service = inject(NotificationService);
  readonly notifications = this.service.notifications;

  /** Default pile (top-right): everything without an explicit bottom-right. */
  readonly topRight = computed<NotificationState[]>(() =>
    this.notifications().filter((n) => (n.position ?? 'top-right') === 'top-right'),
  );

  /** Bottom-right pile: action toasts that would otherwise cover top-right UI. */
  readonly bottomRight = computed<NotificationState[]>(() =>
    this.notifications().filter((n) => n.position === 'bottom-right'),
  );

  /** Below-toolbar pile: refusal toasts whose action lives in the task toolbar. */
  readonly belowToolbar = computed<NotificationState[]>(() =>
    this.notifications().filter((n) => n.position === 'below-toolbar'),
  );

  /** Viewport top of the below-toolbar pile in px; null keeps the stylesheet default. */
  readonly belowToolbarTop = signal<number | null>(null);

  constructor() {
    const cdr = inject(ChangeDetectorRef);
    const unregisterRenderer = this.service.registerRenderer(() => cdr.detectChanges());
    inject(DestroyRef).onDestroy(unregisterRenderer);
    // The toolbar wraps onto a second row on narrow screens, and the refusal
    // banner and the override form appear in the same render as the toast,
    // so the dock is measured after each render while such a toast shows.
    afterEveryRender({ read: () => this.measureToolbarDock() });
    if (typeof document !== 'undefined') {
      const destroyRef = inject(DestroyRef);
      const ac = new AbortController();
      destroyRef.onDestroy(() => ac.abort());
      window.addEventListener('resize', () => this.measureToolbarDock(), { signal: ac.signal });
      // The override form is positioned by the overlay service outside a render.
      const poll = setInterval(() => this.measureToolbarDock(), DOCK_POLL_MS);
      destroyRef.onDestroy(() => clearInterval(poll));
      // Bubble-phase listener. If a modal-stack entry consumed the Escape
      // in capture phase (via stopImmediatePropagation + preventDefault),
      // this handler never fires. Otherwise it dismisses the topmost toast.
      document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;
        if (e.defaultPrevented) return;
        if (this.notifications().length === 0) return;
        e.preventDefault();
        this.service.dismissTopmost();
      }, { signal: ac.signal });
    }
  }

  private measureToolbarDock(): void {
    if (this.belowToolbar().length === 0 || typeof document === 'undefined') return;
    const top = toolbarDockTop(document, window.innerHeight);
    if (top !== this.belowToolbarTop()) this.belowToolbarTop.set(top);
  }

  dismiss(id: number): void {
    this.service.dismiss(id);
  }

  onAction(action: NotificationAction, toastId: number): void {
    action.callback();
    this.service.dismiss(toastId);
  }
}

const DOCK_GAP_PX = 8;
const DOCK_POLL_MS = 250;
/** Room a docked toast needs below a surface before the dock follows that surface. */
const DOCK_TOAST_ROOM_PX = 200;

/** Surfaces under the tab bar that a below-toolbar toast must not cover. */
const DOCK_BELOW = [
  '[data-testid="task-move-refusal"]',
  'app-mobile-lane-primary [data-testid="acceptance-refusal"]',
  '[data-testid="acceptance-refusal-form"]',
];

/**
 * The viewport y below which a below-toolbar toast may sit: under the task
 * tab bar (`studio-tabbar`, which carries the toolbar), and under the card's
 * refusal banner and the open override form while they are on screen. Null
 * when no tab bar renders.
 */
export function toolbarDockTop(root: ParentNode, viewportHeight: number): number | null {
  const bar = root.querySelector('[data-testid="studio-tabbar"]')?.getBoundingClientRect();
  if (!bar || bar.height === 0) return null;
  let bottom = bar.bottom;
  for (const selector of DOCK_BELOW) {
    const box = root.querySelector(selector)?.getBoundingClientRect();
    // A surface scrolled away, or one that leaves no room for the toast below it, is not chased.
    if (box && box.height > 0 && box.top >= bar.bottom - 1 && box.bottom < viewportHeight - DOCK_TOAST_ROOM_PX)
      bottom = Math.max(bottom, box.bottom);
  }
  return Math.round(bottom + DOCK_GAP_PX);
}

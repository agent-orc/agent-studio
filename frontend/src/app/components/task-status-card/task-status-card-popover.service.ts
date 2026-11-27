import {
  ApplicationRef,
  ComponentRef,
  EnvironmentInjector,
  Injectable,
  createComponent,
  inject,
} from '@angular/core';
import { TaskStatusCardComponent } from './task-status-card.component';
import type { TaskInfo } from '../../models/task.model';
import { OverlayPortalRef, OverlayPortalService } from '@studio/ui/services/overlay-portal.service';

const GAP = 8;
const VIEWPORT_PAD = 6;

/**
 * Singleton popover host for `<app-task-status-card>`. Mounts one Angular
 * component instance into the central overlay layer and positions it next to
 * the anchor element. Re-used by:
 *   - Open-tabs hover (Explorer)
 *   - Job-card hover (board)
 *   - any future place that wants a TaskStatusCard popover.
 *
 * The service is intentionally minimal: callers drive `show()` / `hide()`
 * via the `[appTaskStatusPopover]` directive; the directive owns hover
 * timing and ensures the popover is anchored to its host.
 */
@Injectable({ providedIn: 'root' })
export class TaskStatusCardPopover {
  private readonly appRef = inject(ApplicationRef);
  private readonly envInjector = inject(EnvironmentInjector);
  private readonly overlayPortal = inject(OverlayPortalService);

  private host: HTMLDivElement | null = null;
  private portalRef: OverlayPortalRef | null = null;
  private cardRef: ComponentRef<TaskStatusCardComponent> | null = null;
  private currentAnchor: HTMLElement | null = null;
  private hoverIntent = false;

  show(anchor: HTMLElement, job: TaskInfo): void {
    const host = this.ensureHost();
    const card = this.ensureCard();
    this.currentAnchor = anchor;
    card.setInput('job', job);
    card.setInput('variant', 'popover');
    // The card component's host metadata declares `data-testid="task-status-card"`,
    // which overwrites the wrapper host's `task-status-card-popover` testid the
    // moment the component is created. Re-stamp after the card lands so the
    // wrapper stays addressable from outside (Playwright, console queries).
    host.setAttribute('data-testid', 'task-status-card-popover');

    // We move the host off-screen (rather than setting inline
    // `visibility/opacity`) for the size-measurement pass — inline styles
    // would override the `task-status-card-host--visible` class's visibility
    // declarations later and leave the popover painted-but-invisible.
    host.style.left = '-9999px';
    host.style.top = '0px';
    host.classList.add('task-status-card-host--visible');

    // Force a layout pass so we know the natural size.
    card.changeDetectorRef.detectChanges();

    const pos = this.overlayPortal.positionConnected(anchor, host, {
      preferredPlacement: 'right',
      alignment: 'start',
      gap: GAP,
      viewportPadding: VIEWPORT_PAD,
    });
    host.style.left = `${pos.left}px`;
    host.style.top = `${pos.top}px`;
    host.dataset['placement'] = pos.placement;
  }

  hide(anchor: HTMLElement | null): void {
    if (anchor && this.currentAnchor && anchor !== this.currentAnchor) return;
    this.hoverIntent = false;
    if (!this.host) return;
    this.host.classList.remove('task-status-card-host--visible');
    this.currentAnchor = null;
  }

  /**
   * Direct-hover handlers on the popover element so a user can move the
   * pointer from the trigger onto the card without it instantly fading
   * out. The trigger directive calls `markHoverEnter` / `markHoverLeave`
   * on its own mouseenter/leave; the host calls the same when the pointer
   * crosses the card itself.
   */
  markHoverEnter(): void {
    this.hoverIntent = true;
  }

  markHoverLeave(anchor: HTMLElement | null, immediate = false): void {
    if (immediate) {
      this.hide(anchor);
      return;
    }
    this.hoverIntent = false;
    // Defer hide so a quick re-enter (mouse moved between trigger and card)
    // does not cause a flash. The directive handles the 0ms / 80ms timing.
    queueMicrotask(() => {
      if (!this.hoverIntent) this.hide(anchor);
    });
  }

  private ensureHost(): HTMLDivElement {
    if (this.host && document.body.contains(this.host)) return this.host;
    const host = document.createElement('div');
    host.className = 'task-status-card-host';
    host.setAttribute('data-testid', 'task-status-card-popover');
    host.addEventListener('mouseenter', () => this.markHoverEnter());
    host.addEventListener('mouseleave', () => this.markHoverLeave(this.currentAnchor));
    this.portalRef = this.overlayPortal.attachPanel(host);
    this.host = host;
    return host;
  }

  private ensureCard(): ComponentRef<TaskStatusCardComponent> {
    if (this.cardRef && this.host && this.cardRef.location.nativeElement.isConnected) {
      return this.cardRef;
    }
    const ref = createComponent(TaskStatusCardComponent, {
      environmentInjector: this.envInjector,
      hostElement: this.host!,
    });
    this.appRef.attachView(ref.hostView);
    this.cardRef = ref;
    return ref;
  }
}

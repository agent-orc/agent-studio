import { Directive, HostBinding, booleanAttribute, input } from '@angular/core';

/**
 * Shared pending-state contract for native buttons. Consumers keep their
 * existing button styling and content while this directive adds the common
 * disabled, busy, spinner and pending-label behaviour.
 *
 * The directive owns the host `disabled` property: a `[disabled]` binding on
 * the button feeds the `disabled` input below, and the button is disabled
 * while pending or while that binding says so. AGT-3700: an earlier version
 * snapshotted the DOM state when pending began and restored it afterwards,
 * which left a button disabled for good when its own binding flipped during
 * the pending work (a `canSubmit` that includes the busy flag).
 */
@Directive({
  selector: 'button[appPendingButton]',
  standalone: true,
})
export class PendingButtonDirective {
  readonly pending = input(false, { alias: 'appPendingButton' });
  readonly pendingLabel = input('Working…');
  readonly disabled = input(false, { transform: booleanAttribute });

  @HostBinding('disabled')
  get isDisabled(): boolean { return this.pending() || this.disabled(); }

  @HostBinding('class.app-pending-button--pending')
  get pendingClass(): boolean { return this.pending(); }

  @HostBinding('attr.aria-busy')
  get ariaBusy(): string | null { return this.pending() ? 'true' : null; }

  @HostBinding('attr.data-pending-label')
  get label(): string | null { return this.pending() ? this.pendingLabel() : null; }

}

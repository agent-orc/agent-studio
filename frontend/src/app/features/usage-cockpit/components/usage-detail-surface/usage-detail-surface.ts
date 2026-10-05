import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';

import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { UsageCockpitResponse } from '../../models/usage-cockpit.model';
import { cliDisplayName } from '../../usage-chip.util';
import type { UsageDetailFocus } from '../../usage-detail.util';
import { UsageDetailPanelComponent } from '../usage-detail-panel/usage-detail-panel';

/** Phone layout of the Dossier responsive table: 320 to 767 CSS px. */
export const USAGE_PHONE_QUERY = '(max-width: 767px)';

/** Attribute every usage chip carries so focus can return to the visible trigger. */
export const USAGE_TRIGGER_ATTR = 'data-usage-trigger';

export function usageTriggerKey(focus: UsageDetailFocus): string {
  return focus.kind === 'cost' ? 'cost' : `cli:${focus.cliId.toLowerCase()}`;
}

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])';

function visible(el: HTMLElement): boolean {
  return el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
}

/**
 * Usage detail surface (HUC-S3). One native `<dialog>` holding the shared
 * detail panel:
 *
 * - desktop: `show()`, a nonmodal popover anchored under the visible trigger.
 *   Tab may leave it, which closes it without moving focus back; Escape and
 *   the Close button close it and return focus to the trigger.
 * - phone (`USAGE_PHONE_QUERY`): `showModal()`, a bottom sheet. The browser
 *   makes the rest of the page inert; Tab wraps inside the sheet.
 *
 * Triggers are found by `data-usage-trigger` instead of a stored element, so
 * a breakpoint change that swaps the visible chip still anchors and returns
 * focus correctly. A CLI chip hidden at the current width falls back to the
 * first visible usage trigger.
 */
@Component({
  selector: 'app-usage-detail-surface',
  standalone: true,
  imports: [UsageDetailPanelComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-detail-surface.html',
  styleUrl: './usage-detail-surface.scss',
  // Key and focus events bubble from the dialog to this display:contents host.
  host: {
    '(keydown)': 'onKeydown($event)',
    '(focusout)': 'onFocusOut($event)',
    '(document:pointerdown)': 'onDocumentPointerDown($event)',
    '(window:resize)': 'reposition()',
  },
})
export class UsageDetailSurfaceComponent {
  /** Dialog id; chips pass it as `controls`. */
  readonly surfaceId = input('usage-detail');
  readonly snapshot = input<UsageCockpitResponse | null>(null);
  /** Section to open at; `null` keeps the surface closed. */
  readonly focus = input<UsageDetailFocus | null>(null);
  readonly now = input.required<number>();

  /** Emitted on every close; the host clears `focus`. */
  readonly closed = output<void>();

  private readonly dialogRef = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly panelRef = viewChild(UsageDetailPanelComponent);

  readonly phone = signal(false);
  readonly mode = computed(() => (this.phone() ? 'sheet' : 'popover'));
  readonly title = computed(() => {
    const focus = this.focus();
    return !focus || focus.kind === 'cost' ? 'Cost and usage' : `${cliDisplayName(focus.cliId)} usage`;
  });

  /** Last focus that was open, for restoring focus after the dialog closes. */
  private lastFocus: UsageDetailFocus | null = null;
  private openedIn: 'sheet' | 'popover' | null = null;

  constructor() {
    const media = typeof matchMedia === 'function' ? matchMedia(USAGE_PHONE_QUERY) : null;
    if (media) {
      this.phone.set(media.matches);
      const onChange = (e: MediaQueryListEvent) => this.phone.set(e.matches);
      media.addEventListener('change', onChange);
      inject(DestroyRef).onDestroy(() => media.removeEventListener('change', onChange));
    }

    afterRenderEffect(() => {
      const focus = this.focus();
      const mode = this.mode();
      const hasSnapshot = !!this.snapshot();
      untracked(() => this.sync(focus, mode, hasSnapshot));
    });
  }

  private sync(focus: UsageDetailFocus | null, mode: 'sheet' | 'popover', hasSnapshot: boolean): void {
    const dialog = this.dialogRef().nativeElement;
    if (!focus || !hasSnapshot) {
      if (dialog.open) this.closeDialog(false);
      return;
    }
    const sameFocus = this.lastFocus && usageTriggerKey(this.lastFocus) === usageTriggerKey(focus);
    if (dialog.open && this.openedIn === mode) {
      if (!sameFocus) {
        this.lastFocus = focus;
        this.panelRef()?.jumpTo(focus);
      }
      this.reposition();
      return;
    }
    // First open, or a breakpoint change while open: reopen in the new mode and
    // keep focus where it was inside the dialog.
    const reopening = dialog.open;
    const inside = reopening && dialog.contains(document.activeElement) ? document.activeElement as HTMLElement : null;
    if (reopening) dialog.close();
    this.lastFocus = focus;
    this.openedIn = mode;
    if (mode === 'sheet') dialog.showModal(); else dialog.show();
    this.reposition();
    if (inside && dialog.contains(inside)) inside.focus({ preventScroll: true });
    else this.panelRef()?.focusSelected();
  }

  /** The visible trigger for a focus, or the first visible usage trigger. */
  private trigger(focus: UsageDetailFocus | null): HTMLElement | null {
    const all = [...document.querySelectorAll<HTMLElement>(`[${USAGE_TRIGGER_ATTR}]`)].filter(visible);
    if (focus) {
      const key = usageTriggerKey(focus);
      const match = all.find(el => el.getAttribute(USAGE_TRIGGER_ATTR) === key);
      if (match) return match;
    }
    return all[0] ?? null;
  }

  reposition(): void {
    const dialog = this.dialogRef().nativeElement;
    if (!dialog.open || this.openedIn !== 'popover') {
      dialog.style.removeProperty('top');
      dialog.style.removeProperty('left');
      return;
    }
    const anchor = this.trigger(this.lastFocus);
    if (!anchor) return;
    const rect = anchor.getBoundingClientRect();
    const width = dialog.offsetWidth;
    const margin = 8;
    const left = Math.max(margin, Math.min(rect.left, window.innerWidth - width - margin));
    dialog.style.top = `${Math.round(rect.bottom + 4)}px`;
    dialog.style.left = `${Math.round(left)}px`;
  }

  /** Close control, Escape and the native `cancel` event. */
  requestClose(): void {
    this.closeDialog(true);
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.requestClose();
      return;
    }
    if (event.key === 'Tab' && this.openedIn === 'sheet') this.trapTab(event);
    else if (event.key === 'Tab' && !event.shiftKey && this.openedIn === 'popover') this.tabPastEnd(event);
  }

  /**
   * The popover is rendered at the end of the host, not next to its trigger,
   * so Tab from its last control continues after the trigger in page order,
   * as a disclosure would. Shift+Tab leaves naturally through `focusout`.
   */
  private tabPastEnd(event: KeyboardEvent): void {
    const dialog = this.dialogRef().nativeElement;
    const items = [...dialog.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(visible);
    if (items.length === 0 || document.activeElement !== items[items.length - 1]) return;
    const trigger = this.trigger(this.lastFocus);
    if (!trigger) return;
    const order = [...document.querySelectorAll<HTMLElement>(FOCUSABLE)]
      .filter(el => visible(el) && !dialog.contains(el));
    const next = order[order.indexOf(trigger) + 1] ?? null;
    event.preventDefault();
    this.closeDialog(false);
    (next ?? trigger).focus();
  }

  onCancel(event: Event): void {
    // Modal dialogs fire `cancel` on Escape; close through one path.
    event.preventDefault();
    this.requestClose();
  }

  /** Desktop only: Tab leaving the popover closes it and leaves focus where it went. */
  onFocusOut(event: FocusEvent): void {
    if (this.openedIn !== 'popover') return;
    const next = event.relatedTarget as Node | null;
    if (!next || this.dialogRef().nativeElement.contains(next)) return;
    this.closeDialog(false);
  }

  onDocumentPointerDown(event: PointerEvent): void {
    if (this.openedIn !== 'popover') return;
    const dialog = this.dialogRef().nativeElement;
    if (!dialog.open) return;
    const target = event.target as Element | null;
    if (!target || dialog.contains(target) || target.closest(`[${USAGE_TRIGGER_ATTR}]`)) return;
    this.closeDialog(false);
  }

  private trapTab(event: KeyboardEvent): void {
    const dialog = this.dialogRef().nativeElement;
    const items = [...dialog.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(visible);
    if (items.length === 0) return;
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    if (event.shiftKey && (active === first || !dialog.contains(active))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (active === last || !dialog.contains(active))) {
      event.preventDefault();
      first.focus();
    }
  }

  private closeDialog(restoreFocus: boolean): void {
    const dialog = this.dialogRef().nativeElement;
    const focus = this.lastFocus;
    if (dialog.open) dialog.close();
    this.openedIn = null;
    this.lastFocus = null;
    if (restoreFocus) this.trigger(focus)?.focus();
    this.closed.emit();
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  ChangeDetectorRef,
  OnDestroy,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ModalStackService } from '../../services/modal-stack.service';
import { OverlayPortalRef, OverlayPortalService } from '../../services/overlay-portal.service';
import {
  MenuItem,
  MenuItemClickEvent,
  MenuPlacement,
  MenuRow,
} from './menu.types';

/**
 * <app-menu> — F23 shared context-menu surface.
 *
 * One place to render every dropdown / right-click / picker menu in the app.
 * Replaces five hand-rolled menu implementations (devtools menu, studio tab
 * context menu, studio project picker, etc.) so spacing, hover behaviour,
 * keyboard nav, focus management, and theming stay in sync.
 *
 * The component is purely presentational: the caller owns the open signal,
 * provides items, and reacts to `itemClick` + `closeRequest`. Positioning
 * accepts either an `anchorEl` (typical dropdown next to a trigger) or an
 * absolute viewport `position` (right-click context menus). The panel is
 * `position: fixed` so it escapes parent stacking / overflow contexts.
 *
 * Keyboard:
 *   - Esc                 → closeRequest
 *   - ArrowDown / ArrowUp → move focus across rows, skipping headers,
 *                           separators, and disabled rows
 *   - Home / End          → first / last focusable row
 *   - Enter / Space       → activate row → itemClick + closeRequest
 *
 * Aria:
 *   - panel: role="menu"
 *   - row: role="menuitem", aria-disabled, aria-current for active rows
 *   - separator: role="separator"
 *
 * testIds: `{testIdPrefix}-panel` and `{testIdPrefix}-item-{id}` per row.
 *
 * Strictly tokenised SCSS (no raw hex) so dark + light themes stay in sync.
 */
@Component({
  selector: 'app-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './menu.component.html',
  styleUrl: './menu.component.scss',
})
export class MenuComponent implements OnDestroy {
  readonly items = input.required<readonly MenuItem[]>();
  readonly open = input<boolean>(false);
  readonly anchorEl = input<HTMLElement | null>(null);
  /** Viewport-relative coordinate for right-click context menus. */
  readonly position = input<{ x: number; y: number } | null>(null);
  readonly placement = input<MenuPlacement>('below');
  readonly testIdPrefix = input<string>('menu');
  readonly minWidth = input<number | null>(null);
  readonly ariaLabel = input<string | null>(null);

  readonly itemClick = output<MenuItemClickEvent>();
  readonly closeRequest = output<void>();

  @ViewChild('panel', { static: false })
  private panelRef: ElementRef<HTMLDivElement> | null = null;

  private readonly cdr = inject(ChangeDetectorRef);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly modalStack = inject(ModalStackService);
  private readonly overlayPortal = inject(OverlayPortalService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;
  private portalRef: OverlayPortalRef | null = null;
  private repositionAttached = false;
  private readonly reposition = () => this.recomputePosition();

  /** Computed list of focusable row indices (skips header / separator / disabled). */
  readonly focusableIndices = computed<readonly number[]>(() => {
    const out: number[] = [];
    const items = this.items();
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item.kind === 'row' && !item.disabled) out.push(i);
    }
    return out;
  });

  /** Index of the currently focused row in `items()`, or null if none. */
  readonly focusedIndex = signal<number | null>(null);

  /** Inline-style position object applied to the panel. */
  readonly panelStyle = signal<{ top: string; left: string; minWidth?: string }>({
    top: '0px',
    left: '0px',
  });

  constructor() {
    // When the menu opens (or anchor / position changes), recompute the panel
    // position and focus the first focusable row.
    effect(() => {
      const open = this.open();
      if (!open) {
        this.focusedIndex.set(null);
        this.releaseModalStack();
        this.releasePortal();
        return;
      }
      // Register with the modal-stack so Escape closes the menu first instead
      // of skipping past it to whatever modal hosts the menu (e.g. the
      // task-detail panel pushed onto the stack by job-detail). Without this
      // entry the modal-stack's capture-phase Escape handler would close the
      // host before the menu sees the keystroke, and the host would unmount
      // the menu's anchor along with itself.
      this.acquireModalStack();
      this.acquirePortal();
      // Defer to next microtask so the panel exists in the DOM before we
      // measure it / move focus into it.
      queueMicrotask(() => {
        if (!this.open()) return;
        this.recomputePosition();
        const first = this.focusableIndices()[0];
        if (first !== undefined) {
          this.focusedIndex.set(first);
          this.focusRow(first);
        } else {
          this.panelRef?.nativeElement.focus();
        }
        this.cdr.markForCheck();
      });
    });
  }

  ngOnDestroy(): void {
    this.releasePortal();
    this.releaseModalStack();
  }

  private acquireModalStack(): void {
    if (this.modalStackDispose !== null) return;
    this.modalStackDispose = this.modalStack.pushUntilDestroyed(
      `app-menu:${this.testIdPrefix()}`,
      () => {
        this.closeRequest.emit();
        return true;
      },
      this.destroyRef,
      { interactiveRoot: () => this.host.nativeElement },
    );
  }

  private releaseModalStack(): void {
    if (this.modalStackDispose === null) return;
    this.modalStackDispose();
    this.modalStackDispose = null;
  }

  private acquirePortal(): void {
    if (this.portalRef !== null) return;
    this.portalRef = this.overlayPortal.attachPanel(this.host.nativeElement);
    this.modalStack.refreshModalScope();
    this.attachReposition();
  }

  private releasePortal(): void {
    if (this.portalRef === null) return;
    this.detachReposition();
    this.portalRef.dispose();
    this.portalRef = null;
  }

  // ---------------------------------------------------------------------------
  // Click handling
  // ---------------------------------------------------------------------------

  onBackdropClick(): void {
    this.closeRequest.emit();
  }

  onRowClick(index: number): void {
    const items = this.items();
    const item = items[index];
    if (!item || item.kind !== 'row' || item.disabled) return;
    this.itemClick.emit({ id: item.id, item });
    this.closeRequest.emit();
  }

  onRowMouseEnter(index: number): void {
    if (this.focusableIndices().includes(index)) {
      this.focusedIndex.set(index);
      this.focusRow(index);
    }
  }

  // ---------------------------------------------------------------------------
  // Keyboard handling
  // ---------------------------------------------------------------------------

  /**
   * Panel-level keyboard nav. Bound on the panel root so it fires regardless
   * of which row currently has focus.
   */
  onPanelKeyDown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'Escape': {
        event.preventDefault();
        event.stopPropagation();
        this.closeRequest.emit();
        break;
      }
      case 'ArrowDown': {
        event.preventDefault();
        this.moveFocus(+1);
        break;
      }
      case 'ArrowUp': {
        event.preventDefault();
        this.moveFocus(-1);
        break;
      }
      case 'Home': {
        event.preventDefault();
        const first = this.focusableIndices()[0];
        if (first !== undefined) {
          this.focusedIndex.set(first);
          this.focusRow(first);
        }
        break;
      }
      case 'End': {
        event.preventDefault();
        const all = this.focusableIndices();
        const last = all[all.length - 1];
        if (last !== undefined) {
          this.focusedIndex.set(last);
          this.focusRow(last);
        }
        break;
      }
      case 'Enter':
      case ' ': {
        const idx = this.focusedIndex();
        if (idx !== null) {
          event.preventDefault();
          this.onRowClick(idx);
        }
        break;
      }
    }
  }

  /**
   * Global key listener so Escape closes the menu when focus has drifted
   * outside the panel (e.g. anchor button retained focus).
   */
  @HostListener('document:keydown', ['$event'])
  onDocumentKeyDown(event: Event): void {
    if (!this.open()) return;
    const ke = event as KeyboardEvent;
    if (ke.key !== 'Escape') return;
    ke.preventDefault();
    ke.stopPropagation();
    this.closeRequest.emit();
  }

  // ---------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------

  isRow(item: MenuItem): item is MenuRow {
    return item.kind === 'row';
  }

  isFocused(index: number): boolean {
    return this.focusedIndex() === index;
  }

  rowTestId(item: MenuRow): string {
    return `${this.testIdPrefix()}-item-${item.id}`;
  }

  panelTestId(): string {
    return `${this.testIdPrefix()}-panel`;
  }

  // ---------------------------------------------------------------------------
  // Positioning
  // ---------------------------------------------------------------------------

  private recomputePosition(): void {
    const panel = this.panelRef?.nativeElement;
    if (!panel) return;

    const explicit = this.position();
    if (explicit) {
      // Right-click context-menu mode. Clamp to viewport so the panel never
      // renders off-screen below / right of the click point.
      const rect = panel.getBoundingClientRect();
      const vw = window.innerWidth;
      const vh = window.innerHeight;
      const left = Math.min(explicit.x, vw - rect.width - 4);
      const top = Math.min(explicit.y, vh - rect.height - 4);
      this.panelStyle.set({
        top: `${Math.max(4, top)}px`,
        left: `${Math.max(4, left)}px`,
        minWidth: this.minWidthCss(),
      });
      return;
    }

    const anchor = this.anchorEl();
    if (!anchor) {
      // No anchor and no explicit position — fall back to top-left 8/8.
      this.panelStyle.set({ top: '8px', left: '8px', minWidth: this.minWidthCss() });
      return;
    }

    const panelRect = panel.getBoundingClientRect();
    const anchorRect = anchor.getBoundingClientRect();
    const pos = this.overlayPortal.positionConnected(anchor, panel, {
      preferredPlacement: this.placement(),
      alignment: this.placement() === 'below' && panelRect.width > anchorRect.width ? 'end' : 'start',
      gap: 6,
      viewportPadding: 4,
      minWidth: this.minWidth(),
    });

    this.panelStyle.set({
      top: `${pos.top}px`,
      left: `${pos.left}px`,
      minWidth: this.minWidthCss(),
    });
  }

  private attachReposition(): void {
    if (this.repositionAttached) return;
    this.repositionAttached = true;
    window.addEventListener('scroll', this.reposition, true);
    window.addEventListener('resize', this.reposition);
  }

  private detachReposition(): void {
    if (!this.repositionAttached) return;
    this.repositionAttached = false;
    window.removeEventListener('scroll', this.reposition, true);
    window.removeEventListener('resize', this.reposition);
  }

  private minWidthCss(): string | undefined {
    const mw = this.minWidth();
    return mw === null || mw === undefined ? undefined : `${mw}px`;
  }

  private moveFocus(delta: 1 | -1): void {
    const focusable = this.focusableIndices();
    if (focusable.length === 0) return;
    const current = this.focusedIndex();
    let pos = focusable.findIndex(i => i === current);
    if (pos < 0) {
      pos = delta > 0 ? 0 : focusable.length - 1;
    } else {
      pos = (pos + delta + focusable.length) % focusable.length;
    }
    const next = focusable[pos];
    this.focusedIndex.set(next);
    this.focusRow(next);
  }

  private focusRow(index: number): void {
    const panel = this.panelRef?.nativeElement;
    if (!panel) return;
    const el = panel.querySelector<HTMLButtonElement>(
      `[data-menu-row-index="${index}"]`,
    );
    el?.focus({ preventScroll: false });
  }
}

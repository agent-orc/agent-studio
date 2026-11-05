import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

/**
 * Modal-stack arbitration for Escape.
 *
 * Every modal, full-screen overlay, lightbox, and the detail view registers
 * itself with this service while open. A single document-level Escape
 * listener walks the stack from the top (last pushed wins) and invokes
 * the topmost entry's close handler. Lower entries are not touched.
 *
 * Why this exists: before this service every overlay carried its own
 * `@HostListener('document:keydown.escape')`. With two overlays open at
 * once (e.g. "Add Task" on top of "Task Detail") both handlers fired and
 * the lower surface closed under the user. See task
 * `escape-sollte-modals-schliessen`.
 *
 * Contract:
 * - `push(handler)` returns a disposer; the handler stays on the stack
 *   until the disposer is called. Components typically push when they
 *   open and dispose when they close (effect over the `open` signal).
 * - Strict LIFO: the most recently pushed handler is the one Escape
 *   closes. There is no priority field; insertion order is the model.
 * - The close handler returns `boolean | void`. Returning `false`
 *   *declines* the keystroke: the service does NOT stop propagation,
 *   leaving local template bindings (e.g. an inline title-edit
 *   `(keydown.escape)`) free to handle the same Escape. Returning
 *   `true` or `void` is the normal modal-close path: the service
 *   calls `preventDefault` and `stopImmediatePropagation` so legacy
 *   `document:keydown.escape` listeners that have not migrated do not
 *   double-fire on the same keystroke.
 */
@Injectable({ providedIn: 'root' })
export class ModalStackService {
  private readonly entries = signal<readonly ModalEntry[]>([]);
  private readonly inertBefore = new Map<HTMLElement, boolean>();

  /** True when at least one modal/overlay is currently on the stack. */
  readonly hasOpen = computed(() => this.entries().length > 0);

  /** Depth (number of stacked entries). Useful for tests and diagnostics. */
  readonly depth = computed(() => this.entries().length);

  /** Stable id of the entry currently on top, or null when the stack is empty. */
  readonly topId = computed(() => {
    const list = this.entries();
    return list.length === 0 ? null : list[list.length - 1].id;
  });

  private readonly abortController: AbortController | null;

  constructor() {
    if (typeof document === 'undefined') {
      this.abortController = null;
      return;
    }
    this.abortController = new AbortController();
    document.addEventListener('keydown', this.onKeydown, {
      capture: true,
      signal: this.abortController.signal,
    });
    document.addEventListener('focusin', this.onFocusIn, {
      capture: true,
      signal: this.abortController.signal,
    });
    inject(DestroyRef).onDestroy(() => this.abortController?.abort());
  }

  /**
   * Register a close handler on the modal stack. The returned disposer
   * removes the entry; idempotent. Components should call this when they
   * open and the disposer when they close (or via an Angular `effect`).
   *
   * `id` is informational (test ids, telemetry) and never used for
   * ordering — LIFO is the ordering rule.
   *
   * `close` returns `boolean | void`. Returning `false` declines the
   * keystroke (propagation is allowed to continue so local template
   * bindings on the same Escape can still fire). Any other return value
   * consumes the keystroke.
   */
  push(id: string, close: () => boolean | void, scope?: ModalScope | OverlayScope): () => void {
    const entry: ModalEntry = { id, close, scope };
    this.entries.update(list => [...list, entry]);
    this.refreshModalScope();
    return () => {
      this.entries.update(list => list.filter(e => e !== entry));
      this.refreshModalScope();
    };
  }

  /**
   * Convenience: bind a modal entry's lifetime to the owning component
   * via `DestroyRef`. The disposer is called automatically when the
   * component is destroyed, so a forgotten dispose in a teardown branch
   * cannot leave a phantom entry on the stack.
   */
  pushUntilDestroyed(id: string, close: () => boolean | void, destroyRef: DestroyRef, scope?: ModalScope | OverlayScope): () => void {
    const dispose = this.push(id, close, scope);
    destroyRef.onDestroy(dispose);
    return dispose;
  }

  /** Test-only: drains the stack. Production code should never need this. */
  clearForTest(): void {
    this.entries.set([]);
    this.refreshModalScope();
  }

  /** Call after a conditional dialog has rendered so its background becomes inert. */
  refreshModalScope(): void {
    for (const [element, wasInert] of this.inertBefore) element.inert = wasInert;
    this.inertBefore.clear();
    const modal = this.activeModal();
    const root = modal?.backgroundRoot();
    if (!root?.isConnected) return;
    const protectedPaths = new Set<HTMLElement>();
    const roots = [root, ...this.overlayRootsAboveModal()];
    for (const protectedRoot of roots) {
      for (let node: HTMLElement | null = protectedRoot; node; node = node.parentElement) {
        protectedPaths.add(node);
        if (node === document.body) break;
      }
    }
    for (const node of protectedPaths) {
      if (node === document.body) continue;
      const parent: HTMLElement | null = node.parentElement;
      if (!parent) break;
      for (const sibling of Array.from(parent.children)) {
        if (!(sibling instanceof HTMLElement) || protectedPaths.has(sibling) || this.inertBefore.has(sibling)) continue;
        this.inertBefore.set(sibling, sibling.inert);
        sibling.inert = true;
      }
    }
  }

  private activeModal(): ModalScope | undefined {
    const list = this.entries();
    for (let index = list.length - 1; index >= 0; index--) {
      const scope = list[index].scope;
      if (scope && 'dialog' in scope && scope.dialog()?.isConnected) return scope;
    }
    return undefined;
  }

  private overlayRootsAboveModal(): HTMLElement[] {
    const list = this.entries();
    const roots: HTMLElement[] = [];
    for (let index = list.length - 1; index >= 0; index--) {
      const scope = list[index].scope;
      if (scope && 'dialog' in scope && scope.dialog()?.isConnected) break;
      if (scope && 'interactiveRoot' in scope) {
        const root = scope.interactiveRoot();
        if (root?.isConnected) roots.push(root);
      }
    }
    return roots;
  }

  private readonly onFocusIn = (event: FocusEvent): void => {
    const dialog = this.activeModal()?.dialog();
    if (!dialog || dialog.contains(event.target as Node) ||
        this.overlayRootsAboveModal().some(root => root.contains(event.target as Node))) return;
    this.focusBoundary(dialog, false);
  };

  private focusBoundary(dialog: HTMLElement, last: boolean): void {
    const controls = Array.from(dialog.querySelectorAll<HTMLElement>(
      'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])',
    )).filter(element => !element.inert && !element.closest('[inert]') &&
      !element.hasAttribute('disabled') && element.getAttribute('aria-hidden') !== 'true' &&
      element.getClientRects().length > 0 && element.tabIndex >= 0);
    (last ? controls.at(-1) ?? dialog : controls[0] ?? dialog).focus();
  }

  private readonly onKeydown = (event: KeyboardEvent): void => {
    if (event.key === 'Tab') {
      const dialog = this.activeModal()?.dialog();
      if (!dialog) return;
      if (this.overlayRootsAboveModal().some(root => root.contains(document.activeElement))) return;
      const controls = Array.from(dialog.querySelectorAll<HTMLElement>(
        'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])',
      )).filter(element => !element.inert && !element.closest('[inert]') &&
        !element.hasAttribute('disabled') && element.getAttribute('aria-hidden') !== 'true' &&
        element.getClientRects().length > 0 && element.tabIndex >= 0);
      const active = document.activeElement;
      if (!dialog.contains(active) || controls.length === 0 ||
          (event.shiftKey ? active === controls[0] : active === controls.at(-1))) {
        event.preventDefault();
        (event.shiftKey ? controls.at(-1) ?? dialog : controls[0] ?? dialog).focus();
      }
      return;
    }
    if (event.key !== 'Escape') return;
    if (event.defaultPrevented) return;
    if (event.metaKey || event.ctrlKey || event.altKey) return;
    const list = this.entries();
    if (list.length === 0) return;
    const top = list[list.length - 1];
    let handled: unknown;
    try {
      handled = top.close();
    } catch (err) {
      // A faulty close handler must not lock the keystroke. Surface it
      // in the console; the next Escape will still try the new top.
      console.error('[modal-stack] close handler threw', err);
      handled = true;
    }
    if (handled === false) return; // Declined; let local handlers run.
    event.preventDefault();
    event.stopPropagation();
    // Stop legacy listeners on the same capture phase from also handling
    // this key. Without immediate-stop a not-yet-migrated overlay below
    // the stack top would see the event and close itself.
    event.stopImmediatePropagation();
  };
}

/**
 * Helper to bind a signal-typed `open` flag to a modal-stack entry. The
 * `effect` registers when the signal flips true and disposes when it
 * flips false (or when the host component is destroyed via destroyRef).
 *
 * Usage in a component:
 * ```
 * private readonly modalStack = inject(ModalStackService);
 * private readonly destroyRef = inject(DestroyRef);
 * constructor() {
 *   bindOpenToModalStack(this.modalStack, this.open, () => this.close(), 'create-job', this.destroyRef);
 * }
 * ```
 */
interface ModalEntry {
  readonly id: string;
  readonly close: () => boolean | void;
  readonly scope?: ModalScope | OverlayScope;
}

/** A true modal's focus surface and the containing subtree kept interactive. */
export interface ModalScope {
  dialog: () => HTMLElement | null | undefined;
  backgroundRoot: () => HTMLElement | null | undefined;
}

/** A floating child above a modal that remains interactive without releasing the page. */
export interface OverlayScope {
  interactiveRoot: () => HTMLElement | null | undefined;
}

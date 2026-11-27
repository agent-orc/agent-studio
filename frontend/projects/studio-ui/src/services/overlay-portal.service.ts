import { Injectable } from '@angular/core';

export interface OverlayPortalRef {
  readonly element: HTMLElement;
  dispose(): void;
}

export type OverlayPortalLayer = 'panel' | 'modal';
export type OverlayPlacement = 'above' | 'below' | 'right' | 'left';
export type OverlayAlignment = 'start' | 'end';

export interface ConnectedOverlayOptions {
  preferredPlacement?: OverlayPlacement;
  alignment?: OverlayAlignment;
  gap?: number;
  viewportPadding?: number;
  minWidth?: number | null;
}

export interface ConnectedOverlayPosition {
  readonly left: number;
  readonly top: number;
  readonly placement: OverlayPlacement;
}

export interface ConnectedOverlayPositionRef {
  update(): void;
  dispose(): void;
}

const OVERLAY_ROOT_CLASS = 'studio-overlay-root';

/**
 * Central append-to-body primitive for floating UI.
 *
 * Components keep their Angular templates and state, but the actual overlay
 * element is hoisted to <body> so it escapes ancestor overflow, transforms,
 * paint containment, and sibling stacking contexts.
 *
 * Connected popovers / menus also use this service for their anchor
 * positioning. That keeps flip and viewport-clamp behaviour in one place
 * instead of re-implementing near-identical `getBoundingClientRect()` math in
 * every picker.
 */
@Injectable({ providedIn: 'root' })
export class OverlayPortalService {
  private root: HTMLElement | null = null;

  attachLayer(element: HTMLElement, layer: OverlayPortalLayer = 'panel'): OverlayPortalRef {
    return layer === 'modal' ? this.attachModal(element) : this.attachPanel(element);
  }

  attachPanel(element: HTMLElement): OverlayPortalRef {
    return this.attach(
      element,
      'studio-overlay-layer studio-overlay-layer--panel',
      'var(--studio-z-overlay-panel)',
    );
  }

  attachModal(element: HTMLElement): OverlayPortalRef {
    return this.attach(
      element,
      'studio-overlay-layer studio-overlay-layer--modal',
      'var(--studio-z-overlay-modal)',
    );
  }

  attach(
    element: HTMLElement,
    layerClass = 'studio-overlay-layer studio-overlay-layer--panel',
    layerZIndex = 'var(--studio-z-overlay-panel)',
  ): OverlayPortalRef {
    const parent = element.parentNode;
    const next = element.nextSibling;
    const previousPosition = element.style.position;
    const previousZIndex = element.style.zIndex;
    const classes = layerClass.split(/\s+/).filter(Boolean);
    for (const cls of classes) element.classList.add(cls);
    element.style.zIndex = layerZIndex;
    this.overlayRoot().appendChild(element);

    let disposed = false;
    return {
      element,
      dispose: () => {
        if (disposed) return;
        disposed = true;
        for (const cls of classes) element.classList.remove(cls);
        element.style.position = previousPosition;
        element.style.zIndex = previousZIndex;
        if (!parent || !element.isConnected) return;
        if (next && next.parentNode === parent) {
          parent.insertBefore(element, next);
        } else {
          parent.appendChild(element);
        }
      },
    };
  }

  private overlayRoot(): HTMLElement {
    if (this.root?.isConnected) return this.root;
    const existing = document.body.querySelector<HTMLElement>(`:scope > .${OVERLAY_ROOT_CLASS}`);
    if (existing) {
      this.root = existing;
      return existing;
    }
    const root = document.createElement('div');
    root.className = OVERLAY_ROOT_CLASS;
    root.setAttribute('data-testid', 'studio-overlay-root');
    // AGT-3624: the shared tooltip shares the root's z-index and must paint
    // above it (a disabled menu row explains itself in a tooltip), so the
    // root goes in front of an already mounted tooltip host.
    document.body.insertBefore(root, document.body.querySelector(':scope > .cac-tooltip'));
    this.root = root;
    return root;
  }

  positionConnected(
    anchor: HTMLElement,
    panel: HTMLElement,
    options: ConnectedOverlayOptions = {},
  ): ConnectedOverlayPosition {
    const gap = options.gap ?? 6;
    const pad = options.viewportPadding ?? 8;
    const preferred = options.preferredPlacement ?? 'below';
    const alignment = options.alignment ?? 'start';
    const anchorRect = anchor.getBoundingClientRect();
    const panelRect = panel.getBoundingClientRect();
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;

    const panelWidth = Math.max(panelRect.width, options.minWidth ?? 0);
    const panelHeight = panelRect.height;
    const maxLeft = Math.max(pad, viewportWidth - pad - panelWidth);
    const maxTop = Math.max(pad, viewportHeight - pad - panelHeight);

    const candidate = (placement: OverlayPlacement) => {
      switch (placement) {
        case 'above':
          return {
            left: this.alignedLeft(anchorRect, panelWidth, alignment),
            top: anchorRect.top - gap - panelHeight,
          };
        case 'right':
          return { left: anchorRect.right + gap, top: anchorRect.top };
        case 'left':
          return { left: anchorRect.left - gap - panelWidth, top: anchorRect.top };
        case 'below':
        default:
          return {
            left: this.alignedLeft(anchorRect, panelWidth, alignment),
            top: anchorRect.bottom + gap,
          };
      }
    };

    const order = this.placementOrder(preferred);
    let chosen = order[0];
    let pos = candidate(chosen);
    for (const placement of order) {
      const next = candidate(placement);
      if (
        next.left >= pad &&
        next.top >= pad &&
        next.left + panelWidth <= viewportWidth - pad &&
        next.top + panelHeight <= viewportHeight - pad
      ) {
        chosen = placement;
        pos = next;
        break;
      }
    }

    return {
      placement: chosen,
      left: Math.round(this.clamp(pos.left, pad, maxLeft)),
      top: Math.round(this.clamp(pos.top, pad, maxTop)),
    };
  }

  watchConnectedPosition(
    anchor: HTMLElement,
    panel: HTMLElement,
    options: ConnectedOverlayOptions = {},
    apply: (position: ConnectedOverlayPosition) => void = (position) => {
      panel.style.left = `${position.left}px`;
      panel.style.top = `${position.top}px`;
      panel.dataset['placement'] = position.placement;
    },
  ): ConnectedOverlayPositionRef {
    let disposed = false;
    let resizeObserver: ResizeObserver | null = null;

    const update = () => {
      if (disposed || !anchor.isConnected || !panel.isConnected) return;
      apply(this.positionConnected(anchor, panel, options));
    };

    window.addEventListener('scroll', update, true);
    window.addEventListener('resize', update);
    if (typeof ResizeObserver !== 'undefined') {
      resizeObserver = new ResizeObserver(update);
      resizeObserver.observe(panel);
      resizeObserver.observe(anchor);
    }

    queueMicrotask(update);

    return {
      update,
      dispose: () => {
        if (disposed) return;
        disposed = true;
        window.removeEventListener('scroll', update, true);
        window.removeEventListener('resize', update);
        resizeObserver?.disconnect();
      },
    };
  }

  private alignedLeft(anchorRect: DOMRect, panelWidth: number, alignment: OverlayAlignment): number {
    return alignment === 'end' ? anchorRect.right - panelWidth : anchorRect.left;
  }

  private placementOrder(preferred: OverlayPlacement): OverlayPlacement[] {
    switch (preferred) {
      case 'above':
        return ['above', 'below', 'right', 'left'];
      case 'right':
        return ['right', 'left', 'below', 'above'];
      case 'left':
        return ['left', 'right', 'below', 'above'];
      case 'below':
      default:
        return ['below', 'above', 'right', 'left'];
    }
  }

  private clamp(value: number, min: number, max: number): number {
    if (max < min) return min;
    return Math.min(Math.max(value, min), max);
  }
}

import { describe, expect, it } from 'vitest';
import { OverlayPortalService } from './overlay-portal.service';

function rect(init: Partial<DOMRect>): DOMRect {
  const left = init.left ?? 0;
  const top = init.top ?? 0;
  const width = init.width ?? 0;
  const height = init.height ?? 0;
  return {
    x: left,
    y: top,
    left,
    top,
    width,
    height,
    right: init.right ?? left + width,
    bottom: init.bottom ?? top + height,
    toJSON: () => ({}),
  } as DOMRect;
}

describe('OverlayPortalService', () => {
  it('mounts the overlay root before an existing tooltip host so the tooltip paints on top (AGT-3624)', () => {
    const tooltip = document.createElement('div');
    tooltip.className = 'cac-tooltip';
    document.body.appendChild(tooltip);
    const service = new OverlayPortalService();
    const ref = service.attachPanel(document.createElement('section'));
    const root = document.body.querySelector('.studio-overlay-root')!;

    expect(root.compareDocumentPosition(tooltip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    ref.dispose();
    root.remove();
    tooltip.remove();
  });

  it('creates one body-level overlay root for every portaled layer', () => {
    const service = new OverlayPortalService();
    const first = document.createElement('section');
    const second = document.createElement('section');

    const firstRef = service.attachPanel(first);
    const secondRef = service.attachModal(second);
    const roots = Array.from(document.body.querySelectorAll('.studio-overlay-root'));

    expect(roots.length).toBe(1);
    expect(first.parentElement).toBe(roots[0]);
    expect(second.parentElement).toBe(roots[0]);
    expect(roots[0].parentElement).toBe(document.body);
    expect(roots[0].getAttribute('data-testid')).toBe('studio-overlay-root');

    firstRef.dispose();
    secondRef.dispose();
    roots[0].remove();
  });

  it('moves a panel to body and restores it to its original DOM position', () => {
    const service = new OverlayPortalService();
    const host = document.createElement('div');
    const before = document.createElement('span');
    const overlay = document.createElement('section');
    const after = document.createElement('span');
    host.append(before, overlay, after);
    document.body.appendChild(host);

    const ref = service.attachPanel(overlay);
    expect(overlay.parentElement).toBe(document.body.querySelector('.studio-overlay-root'));
    expect(overlay.classList.contains('studio-overlay-layer')).toBe(true);
    expect(overlay.classList.contains('studio-overlay-layer--panel')).toBe(true);
    expect(overlay.style.zIndex).toBe('var(--studio-z-overlay-panel)');

    ref.dispose();
    expect(overlay.parentElement).toBe(host);
    expect(host.children[1]).toBe(overlay);
    expect(overlay.classList.contains('studio-overlay-layer')).toBe(false);
    expect(overlay.style.zIndex).toBe('');

    host.remove();
    document.body.querySelector('.studio-overlay-root')?.remove();
  });

  it('marks modal overlays with the modal layer class', () => {
    const service = new OverlayPortalService();
    const overlay = document.createElement('section');
    document.body.appendChild(overlay);

    const ref = service.attachModal(overlay);
    expect(overlay.parentElement).toBe(document.body.querySelector('.studio-overlay-root'));
    expect(overlay.classList.contains('studio-overlay-layer--modal')).toBe(true);
    expect(overlay.style.zIndex).toBe('var(--studio-z-overlay-modal)');

    ref.dispose();
    overlay.remove();
    document.body.querySelector('.studio-overlay-root')?.remove();
  });

  it('positions connected panels at the anchor and flips when the preferred side is clipped', () => {
    const service = new OverlayPortalService();
    const anchor = document.createElement('button');
    const panel = document.createElement('div');
    anchor.getBoundingClientRect = () => rect({ left: 40, top: 10, width: 80, height: 20 });
    panel.getBoundingClientRect = () => rect({ left: 0, top: 0, width: 120, height: 60 });

    const pos = service.positionConnected(anchor, panel, {
      preferredPlacement: 'above',
      alignment: 'start',
      gap: 6,
      viewportPadding: 8,
    });

    expect(pos.placement).toBe('below');
    expect(pos.left).toBe(40);
    expect(pos.top).toBe(36);
  });

  it('clamps connected panels inside the viewport', () => {
    const service = new OverlayPortalService();
    const anchor = document.createElement('button');
    const panel = document.createElement('div');
    anchor.getBoundingClientRect = () => rect({ left: window.innerWidth - 20, top: window.innerHeight - 20, width: 16, height: 16 });
    panel.getBoundingClientRect = () => rect({ left: 0, top: 0, width: 200, height: 120 });

    const pos = service.positionConnected(anchor, panel, {
      preferredPlacement: 'below',
      alignment: 'start',
      gap: 6,
      viewportPadding: 8,
    });

    expect(pos.left).toBeLessThanOrEqual(window.innerWidth - 8 - 200);
    expect(pos.top).toBeLessThanOrEqual(window.innerHeight - 8 - 120);
    expect(pos.left).toBeGreaterThanOrEqual(8);
    expect(pos.top).toBeGreaterThanOrEqual(8);
  });
});

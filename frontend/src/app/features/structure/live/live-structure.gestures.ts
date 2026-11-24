import type { LiveCamera } from './live-structure.camera';

export interface LiveGestureHandlers {
  /** A press without movement. `double` is the second of two quick ones on the same spot. */
  tap(x: number, y: number, double: boolean): void;
  hover(x: number, y: number): void;
  leave(): void;
  /** The camera moved; a frame is needed. */
  changed(): void;
}

/**
 * Pointer and wheel input of the canvas: drag to pan (with a short glide), wheel
 * to zoom about the cursor, trackpad two-finger pan and pinch, touch pan and
 * two-finger pinch. Returns a function that removes every listener.
 */
export function attachLiveGestures(canvas: HTMLCanvasElement, camera: LiveCamera, handlers: LiveGestureHandlers): () => void {
  const pointers = new Map<number, { x: number; y: number }>();
  let drag: { x0: number; y0: number; x: number; y: number; at: number; moved: boolean; vx: number; vy: number } | null = null;
  let pinch: { distance: number; k: number; wx: number; wy: number } | null = null;
  let lastTap: { at: number; x: number; y: number } | null = null;
  let padUntil = 0;
  const position = (event: { clientX: number; clientY: number }): { x: number; y: number } => {
    const box = canvas.getBoundingClientRect();
    return { x: event.clientX - box.left, y: event.clientY - box.top };
  };
  const spread = (): { distance: number; x: number; y: number } => {
    const [a, b] = [...pointers.values()];
    return { distance: Math.hypot(a.x - b.x, a.y - b.y) || 1, x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  };

  const down = (event: PointerEvent): void => {
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    canvas.setPointerCapture?.(event.pointerId);
    const p = position(event), now = performance.now();
    pointers.set(event.pointerId, p);
    camera.stop();
    if (pointers.size === 1) drag = { x0: p.x, y0: p.y, x: p.x, y: p.y, at: now, moved: false, vx: 0, vy: 0 };
    else if (pointers.size === 2) {
      const s = spread();
      pinch = { distance: s.distance, k: camera.k, wx: camera.wx(s.x), wy: camera.wy(s.y) };
      if (drag) drag.moved = true;
    }
  };
  const move = (event: PointerEvent): void => {
    const p = position(event);
    if (!pointers.has(event.pointerId)) { if (event.pointerType === 'mouse') handlers.hover(p.x, p.y); return; }
    pointers.set(event.pointerId, p);
    if (pinch && pointers.size >= 2) {
      const s = spread();
      camera.pinchTo((pinch.k * s.distance) / pinch.distance, pinch.wx, pinch.wy, s.x, s.y);
      handlers.changed();
      return;
    }
    if (!drag) return;
    const dx = p.x - drag.x, dy = p.y - drag.y, now = performance.now(), dt = Math.max(1, now - drag.at) / 1000;
    if (!drag.moved && Math.hypot(p.x - drag.x0, p.y - drag.y0) > 5) { drag.moved = true; canvas.dataset['dragging'] = 'true'; }
    if (drag.moved) {
      camera.nudge(dx, dy);
      drag.vx = drag.vx * 0.6 + (dx / dt) * 0.4; drag.vy = drag.vy * 0.6 + (dy / dt) * 0.4;
      handlers.changed();
    }
    drag.x = p.x; drag.y = p.y; drag.at = now;
  };
  const up = (event: PointerEvent): void => {
    if (!pointers.has(event.pointerId)) return;
    pointers.delete(event.pointerId);
    const now = performance.now();
    if (pinch) {
      pinch = null;
      const rest = [...pointers.values()][0];
      drag = rest ? { x0: rest.x, y0: rest.y, x: rest.x, y: rest.y, at: now, moved: true, vx: 0, vy: 0 } : null;
      return;
    }
    if (!drag || pointers.size) return;
    delete canvas.dataset['dragging'];
    if (!drag.moved && event.type === 'pointerup') {
      const double = !!lastTap && now - lastTap.at < 340 && Math.hypot(drag.x0 - lastTap.x, drag.y0 - lastTap.y) < 28;
      lastTap = double ? null : { at: now, x: drag.x0, y: drag.y0 };
      handlers.tap(drag.x0, drag.y0, double);
    } else if (drag.moved && now - drag.at < 70 && Math.hypot(drag.vx, drag.vy) > 120) { camera.throw(drag.vx, drag.vy); handlers.changed(); }
    drag = null;
  };
  const wheel = (event: WheelEvent): void => {
    event.preventDefault();
    const p = position(event), now = performance.now();
    const unit = event.deltaMode === 1 ? 33 : event.deltaMode === 2 ? 300 : 1;
    const dy = event.deltaY * unit, dx = event.deltaX * unit;
    const legacy = (event as WheelEvent & { wheelDeltaY?: number }).wheelDeltaY;
    if (event.ctrlKey || event.metaKey) camera.zoomAt(p.x, p.y, Math.exp(-Math.max(-60, Math.min(60, dy)) * 0.012), 0.03);
    else {
      // A wheel notch arrives as large whole steps with no sideways part; a trackpad sends small mixed deltas.
      const notch = event.deltaMode === 1 || (dx === 0 && (legacy !== undefined ? Math.abs(legacy) >= 120 && Math.abs(legacy) % 120 === 0 : Math.abs(dy) >= 50));
      if (notch && now > padUntil) camera.zoomAt(p.x, p.y, Math.exp(-Math.max(-3, Math.min(3, dy / 100)) * 0.24));
      else { padUntil = now + 250; camera.stop(); camera.nudge(-dx, -dy); }
    }
    handlers.changed();
  };
  const leave = (): void => handlers.leave();

  canvas.addEventListener('pointerdown', down);
  canvas.addEventListener('pointermove', move);
  canvas.addEventListener('pointerup', up);
  canvas.addEventListener('pointercancel', up);
  canvas.addEventListener('pointerleave', leave);
  canvas.addEventListener('wheel', wheel, { passive: false });
  return () => {
    canvas.removeEventListener('pointerdown', down);
    canvas.removeEventListener('pointermove', move);
    canvas.removeEventListener('pointerup', up);
    canvas.removeEventListener('pointercancel', up);
    canvas.removeEventListener('pointerleave', leave);
    canvas.removeEventListener('wheel', wheel);
  };
}

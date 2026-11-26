import type { LiveRect } from './live-structure.model';

/** Screen insets the chrome covers; the camera centres what it frames in the rest. */
export interface LiveSafeArea { left: number; top: number; right: number; bottom: number; }
export interface LiveView { x: number; y: number; k: number; }

const clamp = (value: number, low: number, high: number): number => Math.max(low, Math.min(high, value));

/**
 * Camera of the live structure canvas: a world point at the centre of the free
 * area plus a scale. It moves like a map: a drag follows the pointer and glides
 * out, the wheel and a pinch zoom about the pointer, the arrow keys push the
 * view in eased steps that add up while a key is held, and a jump flies there
 * briefly. With reduced motion every change applies at once. `step` returns
 * true while something is still moving, so the host draws frames only then.
 */
export class LiveCamera {
  x = 0; y = 0; k = 1; ox = 0; oy = 0;
  width = 0; height = 0;
  kMin = 0.2; kMax = 4.2;
  reduced = false;
  safe: LiveSafeArea = { left: 0, top: 0, right: 0, bottom: 0 };
  bounds: LiveRect = { x: 0, y: 0, w: 1, h: 1 };
  private fly: { x0: number; y0: number; k0: number; x1: number; y1: number; k1: number; t0: number; dur: number } | null = null;
  private zoomTarget: { k: number; sx: number; sy: number; wx: number; wy: number; tc: number } | null = null;
  private inertia: { vx: number; vy: number } | null = null;
  private push: { x: number; y: number } | null = null;

  readonly sx = (wx: number): number => (wx - this.x) * this.k + this.ox;
  readonly sy = (wy: number): number => (wy - this.y) * this.k + this.oy;
  readonly wx = (sx: number): number => (sx - this.ox) / this.k + this.x;
  readonly wy = (sy: number): number => (sy - this.oy) / this.k + this.y;

  get moving(): boolean { return !!(this.fly || this.zoomTarget || this.inertia || this.push); }
  stop(): void { this.fly = null; this.zoomTarget = null; this.inertia = null; this.push = null; }
  /** Where the camera is, or where the motion in progress will end. */
  get view(): LiveView {
    if (this.fly) return { x: this.fly.x1, y: this.fly.y1, k: this.fly.k1 };
    const zoom = this.zoomTarget;
    if (zoom) return { x: zoom.wx - (zoom.sx - this.ox) / zoom.k, y: zoom.wy - (zoom.sy - this.oy) / zoom.k, k: zoom.k };
    return { x: this.push?.x ?? this.x, y: this.push?.y ?? this.y, k: this.k };
  }

  /** New viewport size or insets. `keep` holds the world point under the old centre. */
  measure(width: number, height: number, safe: LiveSafeArea, keep: boolean): void {
    const ox = this.ox, oy = this.oy;
    this.width = width; this.height = height; this.safe = safe;
    this.ox = safe.left + (width - safe.left - safe.right) / 2;
    this.oy = safe.top + (height - safe.top - safe.bottom) / 2;
    if (keep) { this.x += (this.ox - ox) / this.k; this.y += (this.oy - oy) / this.k; }
  }

  viewFor(rect: LiveRect, maxWidthPx = 0, pad = 0.9): LiveView {
    const sw = Math.max(40, this.width - this.safe.left - this.safe.right);
    const sh = Math.max(40, this.height - this.safe.top - this.safe.bottom);
    let k = Math.min(sw / rect.w, sh / rect.h) * pad;
    if (maxWidthPx) k = Math.min(k, maxWidthPx / rect.w);
    return { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2, k: clamp(k, this.kMin, this.kMax) };
  }

  /** Limits follow the content: far enough out to see everything, close enough for the last level. */
  setBounds(bounds: LiveRect): void {
    this.bounds = bounds;
    this.kMin = 0.02;
    this.kMin = this.viewFor(bounds, 0, 0.97).k * 0.72;
  }

  go(view: LiveView, instant = false): void {
    this.stop();
    if (instant || this.reduced) { this.x = view.x; this.y = view.y; this.k = view.k; return; }
    const distance = Math.hypot(view.x - this.x, view.y - this.y) * this.k;
    // Brief, and a little longer for a long way, so the eye can follow where the view goes.
    this.fly = { x0: this.x, y0: this.y, k0: this.k, x1: view.x, y1: view.y, k1: view.k, t0: performance.now(),
      dur: clamp(200 + 140 * Math.abs(Math.log(view.k / this.k)) + distance / 8, 220, 450) };
  }
  fitAll(instant = false): void { this.go(this.viewFor(this.bounds, 0, 0.97), instant); }
  fitRect(rect: LiveRect, leaf: boolean, instant = false): void {
    const phone = this.width <= 720;
    this.go(this.viewFor(rect, leaf ? (phone ? this.width - 24 : 720) : 0, leaf ? (phone ? 0.94 : 0.86) : 0.92), instant);
  }
  /** A keyboard step. Steps add up while a key repeats, and the view eases after them without restarting. */
  panBy(dxScreen: number, dyScreen: number): void {
    const from = this.push ?? { x: this.x, y: this.y }, b = this.bounds;
    this.fly = null; this.inertia = null; this.zoomTarget = null;
    this.push = { x: clamp(from.x + dxScreen / this.k, b.x, b.x + b.w), y: clamp(from.y + dyScreen / this.k, b.y, b.y + b.h) };
    if (this.reduced) { this.x = this.push.x; this.y = this.push.y; this.push = null; }
  }
  /** Moves without easing, for drags and trackpad pans. */
  nudge(dxScreen: number, dyScreen: number): void { this.x -= dxScreen / this.k; this.y -= dyScreen / this.k; this.clampCentre(); }
  centreOn(wx: number, wy: number): void { this.stop(); this.x = wx; this.y = wy; this.clampCentre(); }
  throw(vx: number, vy: number): void {
    if (!this.reduced) this.inertia = { vx: clamp(vx, -3000, 3000), vy: clamp(vy, -3000, 3000) };
  }

  /** Zooms about a screen point, so what is under the cursor stays there. */
  zoomAt(sx: number, sy: number, factor: number, timeConstant = 0.085): void {
    this.fly = null; this.inertia = null; this.push = null;
    const k = clamp((this.zoomTarget ? this.zoomTarget.k : this.k) * factor, this.kMin, this.kMax);
    this.zoomTarget = { k, sx, sy, wx: this.wx(sx), wy: this.wy(sy), tc: timeConstant };
    if (this.reduced) { this.applyZoom(k); this.zoomTarget = null; }
  }
  /** Pinch: the scale and the world point under the pinch centre are set directly. */
  pinchTo(k: number, wx: number, wy: number, sx: number, sy: number): void {
    this.stop();
    this.k = clamp(k, this.kMin, this.kMax);
    this.x = wx - (sx - this.ox) / this.k; this.y = wy - (sy - this.oy) / this.k;
    this.clampCentre();
  }

  private applyZoom(k: number): void {
    const target = this.zoomTarget!;
    this.k = k; this.x = target.wx - (target.sx - this.ox) / k; this.y = target.wy - (target.sy - this.oy) / k;
  }
  private clampCentre(): void {
    const b = this.bounds;
    this.x = clamp(this.x, b.x, b.x + b.w); this.y = clamp(this.y, b.y, b.y + b.h);
  }

  /** Advances the running motion. Returns true while another frame is needed. */
  step(now: number, dt: number): boolean {
    if (this.fly) {
      const fly = this.fly;
      const u = clamp((now - fly.t0) / fly.dur, 0, 1), e = u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2;
      const k = fly.k0 * Math.pow(fly.k1 / fly.k0, e);
      if (Math.abs(fly.k1 - fly.k0) / fly.k0 > 0.02) {
        // Zoom about the one world point that keeps its screen position, so the flight does not swoop.
        const fx = (fly.x0 * fly.k0 - fly.x1 * fly.k1) / (fly.k0 - fly.k1), fy = (fly.y0 * fly.k0 - fly.y1 * fly.k1) / (fly.k0 - fly.k1);
        this.x = fx - ((fx - fly.x0) * fly.k0) / k; this.y = fy - ((fy - fly.y0) * fly.k0) / k;
      } else { this.x = fly.x0 + (fly.x1 - fly.x0) * e; this.y = fly.y0 + (fly.y1 - fly.y0) * e; }
      this.k = k;
      if (u >= 1) { this.x = fly.x1; this.y = fly.y1; this.k = fly.k1; this.fly = null; }
    } else if (this.zoomTarget) {
      const target = this.zoomTarget;
      const logK = Math.log(this.k) + (Math.log(target.k) - Math.log(this.k)) * (1 - Math.exp(-dt / target.tc));
      this.applyZoom(Math.exp(logK));
      if (Math.abs(Math.log(target.k / this.k)) < 0.002) { this.applyZoom(target.k); this.zoomTarget = null; }
    } else if (this.inertia) {
      this.x -= (this.inertia.vx * dt) / this.k; this.y -= (this.inertia.vy * dt) / this.k;
      const decay = Math.exp(-dt / 0.22);
      this.inertia.vx *= decay; this.inertia.vy *= decay;
      if (Math.hypot(this.inertia.vx, this.inertia.vy) < 12) this.inertia = null;
    } else if (this.push) {
      const f = 1 - Math.exp(-dt / 0.07);
      this.x += (this.push.x - this.x) * f; this.y += (this.push.y - this.y) * f;
      if (Math.hypot(this.push.x - this.x, this.push.y - this.y) * this.k < 0.5) { this.x = this.push.x; this.y = this.push.y; this.push = null; }
    }
    this.clampCentre();
    return this.moving;
  }
}

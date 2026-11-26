import type { StatusTone } from '../../../components/status-indicator/status-indicator.component';

/** Colours and fonts of the canvas. Every entry is read from a Studio token; none is defined here. */
export interface LivePalette {
  outside: string; inside: string; s1: string; s2: string; s3: string;
  ink: string; ink2: string; ink3: string; f1: string; f2: string; f3: string; grid: string; track: string;
  perf: string; net: string; storage: string; backup: string;
  warn: string; warnBg: string; danger: string; dangerBg: string; ok: string; info: string; onTone: string;
  fontUi: string; fontMono: string;
}
const COLOR_KEYS = ['outside', 'inside', 's1', 's2', 's3', 'ink', 'ink2', 'ink3', 'f1', 'f2', 'f3', 'grid', 'track',
  'perf', 'net', 'storage', 'backup', 'warn', 'warnBg', 'danger', 'dangerBg', 'ok', 'info', 'onTone'] as const;
const kebab = (key: string): string => key.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`);

/**
 * Resolves the `--lsv-*` custom properties of the host (each one aliases a Studio
 * token in the component stylesheet) into colour strings a canvas accepts. A probe
 * element lets the browser resolve `var()` and `color-mix()` for the current theme.
 */
export function readLivePalette(host: HTMLElement): LivePalette {
  const probe = document.createElement('span');
  probe.style.position = 'absolute';
  probe.style.visibility = 'hidden';
  host.appendChild(probe);
  const out: Partial<Record<keyof LivePalette, string>> = {};
  for (const key of COLOR_KEYS) {
    probe.style.color = `var(--lsv-${kebab(key)})`;
    out[key] = getComputedStyle(probe).color;
  }
  // Names use the font the Studio around the canvas uses; values use the Studio's mono token.
  out.fontUi = getComputedStyle(host).fontFamily;
  probe.style.fontFamily = 'var(--font-mono)';
  out.fontMono = getComputedStyle(probe).fontFamily;
  probe.remove();
  return out as LivePalette;
}

export const clamp = (value: number, low: number, high: number): number => Math.max(low, Math.min(high, value));

/** Canvas 2D primitives shared by the renderer: measured text, shapes, hatching and the health marks. */
export class LivePainter {
  pal!: LivePalette;
  dpr = 1;
  private readonly widths = new Map<string, number>();
  private hatches = new Map<string, CanvasPattern>();

  constructor(readonly ctx: CanvasRenderingContext2D) {}

  setPalette(palette: LivePalette): void { this.pal = palette; this.widths.clear(); this.hatches = new Map(); }
  begin(width: number, height: number): void {
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.ctx.globalAlpha = 1;
    this.ctx.clearRect(0, 0, width, height);
  }

  font(weight: number, size: number, mono = false): string { return `${weight} ${size.toFixed(2)}px ${mono ? this.pal.fontMono : this.pal.fontUi}`; }
  tw(text: string, font: string): number {
    const key = `${font}|${text}`;
    let width = this.widths.get(key);
    if (width === undefined) {
      this.ctx.font = font;
      width = this.ctx.measureText(text).width;
      if (this.widths.size > 6000) this.widths.clear();
      this.widths.set(key, width);
    }
    return width;
  }
  /** The first wording that fits, or null. Text is never shortened with an ellipsis. */
  pick(candidates: readonly (string | null | undefined)[], max: number, font: string): string | null {
    for (const candidate of candidates) if (candidate && this.tw(candidate, font) <= max) return candidate;
    return null;
  }
  /** The text on one line, or broken at a space into two. Null when it fits neither way; nothing is cut. */
  wrap(value: string, max: number, font: string, lines: number): string[] | null {
    if (this.tw(value, font) <= max) return [value];
    if (lines < 2) return null;
    const words = value.split(' ');
    for (let split = words.length - 1; split >= 1; split--) {
      const first = words.slice(0, split).join(' '), second = words.slice(split).join(' ');
      if (this.tw(first, font) <= max && this.tw(second, font) <= max) return [first, second];
    }
    return null;
  }
  /** The text broken at spaces into as many lines as it needs. Null when one word alone is too wide; nothing is cut. */
  lines(value: string, max: number, font: string): string[] | null {
    const out: string[] = [];
    let line = '';
    for (const word of value.split(' ')) {
      const next = line ? `${line} ${word}` : word;
      if (this.tw(next, font) <= max) { line = next; continue; }
      if (!line || this.tw(word, font) > max) return null;
      out.push(line);
      line = word;
    }
    if (line) out.push(line);
    return out;
  }
  text(value: string, x: number, y: number, font: string, color: string, align: CanvasTextAlign = 'left'): void {
    const ctx = this.ctx;
    ctx.font = font; ctx.fillStyle = color; ctx.textAlign = align; ctx.textBaseline = 'middle';
    ctx.fillText(value, x, y + 0.5);
  }

  rrect(x: number, y: number, w: number, h: number, radius: number): void {
    const ctx = this.ctx, r = Math.max(0, Math.min(radius, w / 2, h / 2));
    ctx.beginPath();
    ctx.moveTo(x + r, y); ctx.lineTo(x + w - r, y); ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r); ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x + r, y + h); ctx.arcTo(x, y + h, x, y + h - r, r);
    ctx.lineTo(x, y + r); ctx.arcTo(x, y, x + r, y, r); ctx.closePath();
  }
  /** Host service: a box with one chamfered corner. */
  notched(x: number, y: number, w: number, h: number, cut: number, radius: number): void {
    const ctx = this.ctx, c = Math.min(cut, w / 3, h / 2), r = Math.max(0, Math.min(radius, w / 2, h / 2));
    ctx.beginPath();
    ctx.moveTo(x + c, y); ctx.lineTo(x + w - r, y); ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r); ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x + r, y + h); ctx.arcTo(x, y + h, x, y + h - r, r);
    ctx.lineTo(x, y + c); ctx.closePath();
  }
  tri(cx: number, cy: number, s: number, color: string): void {
    const ctx = this.ctx;
    ctx.beginPath(); ctx.moveTo(cx, cy - s); ctx.lineTo(cx + s * 1.05, cy + s * 0.85); ctx.lineTo(cx - s * 1.05, cy + s * 0.85); ctx.closePath();
    ctx.fillStyle = color; ctx.fill();
  }
  hatch(color: string, alpha: number): CanvasPattern {
    const key = `${color}|${alpha}|${this.dpr}`;
    const cached = this.hatches.get(key);
    if (cached) return cached;
    const tile = document.createElement('canvas'), s = Math.round(6 * this.dpr);
    tile.width = tile.height = s;
    const g = tile.getContext('2d')!;
    g.strokeStyle = color; g.globalAlpha = alpha; g.lineWidth = Math.max(1, this.dpr);
    g.beginPath(); g.moveTo(-1, s + 1); g.lineTo(s + 1, -1); g.moveTo(-1, 1); g.lineTo(1, -1); g.moveTo(s - 1, s + 1); g.lineTo(s + 1, s - 1); g.stroke();
    const pattern = this.ctx.createPattern(tile, 'repeat')!;
    pattern.setTransform(new DOMMatrix().scale(1 / this.dpr));
    this.hatches.set(key, pattern);
    return pattern;
  }

  /**
   * Health mark, one shape per tone so the meaning never rests on colour:
   * disc with a check (up), triangle (needs attention), square with a cross
   * (down), empty ring (not observed).
   */
  mark(cx: number, cy: number, tone: StatusTone): void {
    const ctx = this.ctx, pal = this.pal;
    ctx.save();
    ctx.lineWidth = 1.5; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    if (tone === 'success') {
      ctx.beginPath(); ctx.arc(cx, cy, 5, 0, Math.PI * 2); ctx.fillStyle = pal.ok; ctx.fill();
      ctx.strokeStyle = pal.onTone; ctx.beginPath(); ctx.moveTo(cx - 2.4, cy + 0.2); ctx.lineTo(cx - 0.6, cy + 2); ctx.lineTo(cx + 2.6, cy - 1.8); ctx.stroke();
    } else if (tone === 'warning') {
      this.tri(cx, cy + 0.5, 5.5, pal.warn);
      ctx.fillStyle = pal.onTone; ctx.fillRect(cx - 0.7, cy - 2.2, 1.4, 3.4); ctx.fillRect(cx - 0.7, cy + 2.2, 1.4, 1.4);
    } else if (tone === 'danger') {
      ctx.fillStyle = pal.danger; ctx.fillRect(cx - 4.5, cy - 4.5, 9, 9);
      ctx.strokeStyle = pal.onTone; ctx.beginPath(); ctx.moveTo(cx - 2, cy - 2); ctx.lineTo(cx + 2, cy + 2); ctx.moveTo(cx + 2, cy - 2); ctx.lineTo(cx - 2, cy + 2); ctx.stroke();
    } else {
      ctx.strokeStyle = tone === 'info' || tone === 'running' ? pal.info : pal.ink3;
      ctx.beginPath(); ctx.arc(cx, cy, 4.2, 0, Math.PI * 2); ctx.stroke();
    }
    ctx.restore();
  }

  /** Path along screen points, optionally offset sideways (the two walls of a tunnel). */
  trace(points: readonly (readonly [number, number])[], offset = 0): void {
    const ctx = this.ctx;
    const unit = (a: readonly [number, number], b: readonly [number, number]): [number, number] => {
      const dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1;
      return [dx / l, dy / l];
    };
    ctx.beginPath();
    for (let i = 0; i < points.length; i++) {
      let [px, py] = points[i];
      if (offset) {
        const d1 = i > 0 ? unit(points[i - 1], points[i]) : null, d2 = i < points.length - 1 ? unit(points[i], points[i + 1]) : null;
        const n1 = d1 ? [-d1[1], d1[0]] : null, n2 = d2 ? [-d2[1], d2[0]] : null;
        const straight = !!n1 && !!n2 && Math.abs(n1[0] - n2[0]) < 0.01 && Math.abs(n1[1] - n2[1]) < 0.01;
        const scale = straight ? 0.5 : 1;
        px += ((n1?.[0] ?? 0) + (n2?.[0] ?? 0)) * offset * scale;
        py += ((n1?.[1] ?? 0) + (n2?.[1] ?? 0)) * offset * scale;
      }
      if (i) ctx.lineTo(px, py); else ctx.moveTo(px, py);
    }
  }
}

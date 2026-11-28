import type { LiveCamera } from './live-structure.camera';
import { canvasFacts, canvasGaps, levelOf, nodeFacts, type LiveFact, type LiveGap, type LiveLevel } from './live-structure.facts';
import type { LiveLayout, LiveRoute } from './live-structure.layout';
import { COVERAGE_WORD, diskPressure, diskUsed, gib, percent, size, subtreeWarnings, type LiveLayer, type LiveLink, type LiveNode, type LiveRect, type LiveScene } from './live-structure.model';
import { LivePainter, clamp, type LivePalette } from './live-structure.painter';

export interface LiveRenderState {
  layers: Readonly<Record<LiveLayer, boolean>>;
  /** Active layers, the one switched on last first. Decides which facts a zoom level adds first. */
  order: readonly LiveLayer[];
  selected: string | null; selectedLink: string | null;
  hover: string | null; hoverLink: string | null;
}
export type LiveHit = { node: string; card?: string; link?: undefined } | { link: string; node?: undefined; card?: undefined };

const isFrame = (node: LiveNode): boolean => node.kind === 'machine' || node.kind === 'vm' || node.kind === 'group';
const LAYER_COLOR: Readonly<Record<LiveLayer, keyof LivePalette>> = { perf: 'perf', network: 'net', storage: 'storage', backup: 'backup', warnings: 'warn' };

/**
 * Immediate-mode renderer of the live structure canvas. Every node lays out its
 * content for the size it has on screen right now: type and stroke sizes stay
 * constant, and only the number of facts changes with the zoom (semantic zoom).
 * Text is never shrunk or cut: a wording that does not fit is replaced by a
 * shorter one, and a fact with no fitting wording is left out at that level.
 * From the container level on no node is a bare box: it shows facts, and for
 * what the Studio does not know it says what is not observed and why.
 */
export class LiveRenderer {
  private readonly p: LivePainter;
  private scene: LiveScene | null = null;
  private layout: LiveLayout | null = null;
  private state!: LiveRenderState;
  private width = 0;
  private height = 0;
  /** Level each node was last drawn at, for the level read-out and the tests. */
  readonly levels = new Map<string, LiveLevel>();
  /** Facts and "not observed" sentences each node carried in the last frame it was drawn in. */
  readonly shown = new Map<string, number>();
  /** Number of frames drawn, so a test can prove an idle canvas draws nothing. */
  frames = 0;
  /** Route labels placed in this frame; a label that would cover one of them is left out. */
  private cardHits: { node: string; card: string; rect: LiveRect }[] = [];
  private labels: LiveRect[] = [];

  constructor(private readonly canvas: HTMLCanvasElement, private readonly cam: LiveCamera) {
    this.p = new LivePainter(canvas.getContext('2d')!);
  }

  setPalette(palette: LivePalette): void { this.p.setPalette(palette); }
  setScene(scene: LiveScene, layout: LiveLayout): void { this.scene = scene; this.layout = layout; this.levels.clear(); this.shown.clear(); }
  resize(width: number, height: number): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    this.width = width; this.height = height;
    this.canvas.width = Math.round(width * dpr); this.canvas.height = Math.round(height * dpr);
    this.p.dpr = dpr;
  }

  rect(r: LiveRect): LiveRect {
    const x = Math.round(this.cam.sx(r.x)) + 0.5, y = Math.round(this.cam.sy(r.y)) + 0.5;
    return { x, y, w: Math.round(this.cam.sx(r.x + r.w)) + 0.5 - x, h: Math.round(this.cam.sy(r.y + r.h)) + 0.5 - y };
  }
  private visible(r: LiveRect): boolean { return r.x < this.width + 30 && r.y < this.height + 30 && r.x + r.w > -30 && r.y + r.h > -30; }

  draw(state: LiveRenderState): void {
    const scene = this.scene, layout = this.layout, p = this.p, pal = p.pal;
    if (!scene || !layout || !pal) return;
    this.state = state;
    this.frames++;
    this.labels = [];
    this.cardHits = [];
    p.begin(this.width, this.height);
    p.ctx.fillStyle = pal.outside; p.ctx.fillRect(0, 0, this.width, this.height);
    const b = this.rect(scene.boundary.rect), k = this.cam.k;
    p.rrect(b.x, b.y, b.w, b.h, 14 * Math.min(1, k)); p.ctx.fillStyle = pal.inside; p.ctx.fill();
    this.grid();
    this.boundary(b, scene.boundary);
    // A machine that holds nothing is drawn like a unit: a box with a name, not an empty frame.
    const holds = (node: LiveNode): boolean => isFrame(node) && node.children.length > 0;
    const frames = scene.order.filter(holds), units = scene.order.filter(node => !holds(node) && node.kind !== 'boundary');
    for (const node of frames) this.frame(node);
    for (const route of layout.routes) this.linkStroke(route);
    for (const node of units) this.unit(node);
    for (const route of layout.routes) this.linkMarks(route);
    // Labels last, so no port or arrowhead of another route is drawn across one.
    for (const route of layout.routes) this.linkLabel(route);
    this.rings();
    if (state.layers.warnings) for (const node of scene.order) this.pin(node);
  }

  private grid(): void {
    const { ctx, pal } = this.p, cam = this.cam, k = cam.k, minor = 20 * k;
    const x0 = cam.wx(0), y0 = cam.wy(0), x1 = cam.wx(this.width), y1 = cam.wy(this.height);
    ctx.fillStyle = pal.grid;
    if (minor >= 12) {
      ctx.globalAlpha = clamp((minor - 12) / 14, 0, 1) * 0.7;
      for (let gx = Math.ceil(x0 / 20) * 20; gx < x1; gx += 20) { const sx = Math.round(cam.sx(gx)); for (let gy = Math.ceil(y0 / 20) * 20; gy < y1; gy += 20) ctx.fillRect(sx, Math.round(cam.sy(gy)), 1, 1); }
    }
    ctx.globalAlpha = clamp((100 * k) / 60, 0.35, 1);
    for (let gx = Math.ceil(x0 / 100) * 100; gx < x1; gx += 100) { const sx = Math.round(cam.sx(gx)); for (let gy = Math.ceil(y0 / 100) * 100; gy < y1; gy += 100) { const sy = Math.round(cam.sy(gy)); ctx.fillRect(sx - 2, sy, 5, 1); ctx.fillRect(sx, sy - 2, 1, 5); } }
    ctx.globalAlpha = 1;
  }

  private boundary(b: LiveRect, node: LiveNode): void {
    const { ctx, pal } = this.p, p = this.p;
    ctx.save();
    ctx.strokeStyle = pal.f1; ctx.lineWidth = 1.25; ctx.setLineDash([12, 4, 2, 4]);
    p.rrect(b.x, b.y, b.w, b.h, 14 * Math.min(1, this.cam.k)); ctx.stroke();
    ctx.restore();
    const font = p.font(500, 11, true);
    const label = p.pick([`System boundary · ${node.name}`, 'System boundary'], b.w * 0.7 - 40, font);
    if (!label) return;
    const lx = b.x + clamp(26 * this.cam.k, 12, 26), lw = p.tw(label, font) + 14;
    ctx.fillStyle = pal.inside; ctx.fillRect(lx, b.y - 8, lw, 16);
    p.text(label, lx + 7, b.y, font, pal.ink2);
  }

  /** Machine, virtual machine or group: the shape, then a header that holds as many facts as its size allows. */
  private frame(node: LiveNode): void {
    const r = this.rect(node.rect);
    if (!this.visible(r)) return;
    const { ctx, pal } = this.p, p = this.p, k = this.cam.k;
    const strong = node.kind === 'machine';
    const radius = clamp((strong ? 12 : 9) * k, 3, strong ? 12 : 9);
    if (node.kind === 'group') {
      ctx.save(); ctx.setLineDash([5, 4]); ctx.strokeStyle = pal.f2; ctx.lineWidth = 1; p.rrect(r.x, r.y, r.w, r.h, radius); ctx.stroke(); ctx.restore();
    } else {
      p.rrect(r.x, r.y, r.w, r.h, radius); ctx.fillStyle = strong ? pal.s1 : pal.s2; ctx.fill();
      ctx.strokeStyle = strong ? pal.f1 : pal.f2; ctx.lineWidth = strong ? 1.5 : 1; ctx.stroke();
      // A virtual machine is drawn with a double line.
      if (node.kind === 'vm' && r.w > 60) { p.rrect(r.x + 3, r.y + 3, r.w - 6, r.h - 6, Math.max(1, radius - 2)); ctx.stroke(); }
    }
    const head = node.headH * k, level = levelOf(node, r.w, r.h, k);
    this.levels.set(node.id, level);
    if (head < 18 || r.w < 48) return;
    const pad = clamp(r.w * 0.035, 7, 18), x = r.x + pad, right = r.x + r.w - pad;
    const ts = clamp((strong ? 11 : 10.5) + r.w * 0.011, 12, strong ? 17 : 15);
    const two = head >= ts + 26 && r.w >= 170;
    const cy = two ? r.y + Math.min(head / 2 - 8, pad + ts / 2 + 4) : r.y + Math.max(head / 2, ts / 2 + 5);
    const titleFont = p.font(600, ts), subFont = p.font(400, 11, true);
    const warnings = this.state.layers.warnings ? subtreeWarnings(node).length : 0;
    const badge = warnings ? 34 : 0;
    const name = p.pick([node.name, node.short], right - x - 18 - badge, titleFont);
    p.mark(x + 6, cy, node.health.tone);
    if (!name) return;
    p.text(name, x + 18, cy, titleFont, pal.ink);
    const nameEnd = x + 18 + p.tw(name, titleFont);
    if (warnings) { p.tri(nameEnd + 14, cy, 5.5, pal.warn); p.text(String(warnings), nameEnd + 23, cy, p.font(600, 11.5, true), pal.warn); }
    let left = nameEnd + badge + 18;
    // Performance layer at the far level: a load gauge without text.
    if (level < 2 && this.state.layers.perf && node.perf) {
      const perf = node.perf, room = right - left;
      const specs = [perf.cores ? perf.cores + ' cores' : '', perf.memTotal ? gib(perf.memTotal) : ''].filter(Boolean).join(' · ');
      const value = p.pick([specs, perf.cpu != null ? percent(perf.cpu) : null], room, subFont);
      if (value) p.text(value, right, cy, subFont, pal.ink2, 'right');
      else if (perf.cpu != null && room >= 44) this.gauge(right - 44, cy - 2, 44, 4, perf.cpu);
    }
    if (two) {
      const role = p.pick([node.role, node.role.split(' · ')[0]], right - x, subFont);
      if (role) { p.text(role, x, cy + ts / 2 + 10, subFont, pal.ink2); left = Math.max(left, x + p.tw(role, subFont) + 18); }
    }
    if (level < 2) return;
    // Facts sit to the right of the title, in columns over the full header height.
    const space = right - left, columns = Math.floor((space + 16) / 216);
    if (columns < 1) return;
    this.shown.set(node.id, this.flow(canvasFacts(node, this.scene!, level, this.state.order), canvasGaps(node, this.scene!, level, this.state.order),
      left, r.y + 7, space, head - 12, columns, 16));
  }

  /**
   * Sets facts and "not observed" sentences into columns and returns how many it drew. Facts come
   * first, spread evenly over the columns in reading order. The foot of the last column is kept
   * for the first sentence, so a node still says what it lacks when its facts alone would fill
   * it. A fact or sentence that does not fit is left out.
   */
  private flow(facts: readonly LiveFact[], gaps: readonly LiveGap[], x: number, y: number, w: number, h: number, columns: number, gutter: number): number {
    const p = this.p, colW = (w - (columns - 1) * gutter) / columns, bottom = y + h + 0.5, line = 14, pitch = 18;
    const font = p.font(400, 11, true);
    const wordings = (gap: LiveGap): string[][] => gap.values.map(value => p.lines(value, colW - 13, font)).filter((lines): lines is string[] => !!lines);
    const first = gaps.length ? wordings(gaps[0]) : [];
    const least = first.length ? Math.min(...first.map(lines => lines.length)) * line + 2 : 0;
    const reserve = least <= h * 0.5 ? least : 0;
    const room = (column: number): number => Math.max(0, Math.floor((h + 3.5 - (column === columns - 1 ? reserve : 0)) / pitch));
    const capacity = Array.from({ length: columns }, (_, column) => room(column)).reduce((sum, rows) => sum + rows, 0);
    const each = Math.ceil(Math.min(facts.length, capacity) / columns);
    const cursor = Array.from({ length: columns }, () => y), rows = Array.from({ length: columns }, () => 0);
    let column = 0, drawn = 0;
    for (const fact of facts) {
      while (column < columns && rows[column] >= Math.min(each, room(column))) column++;
      if (column >= columns) break;
      const fh = fact.cells ? 16 : 15;
      if (!this.factRow(fact, x + column * (colW + gutter), cursor[column], colW, fh)) continue;
      cursor[column] += pitch; rows[column]++; drawn++;
    }
    for (const gap of gaps) {
      // Under the shortest column, the rightmost of equals: after the facts in reading order.
      const at = cursor.reduce((best, value, index) => value <= cursor[best] ? index : best, 0);
      const lines = (gap === gaps[0] ? first : wordings(gap)).find(option => cursor[at] + option.length * line <= bottom);
      if (!lines) continue;
      // The empty ring is the mark of "not observed" everywhere in the view.
      const gx = x + at * (colW + gutter), cy = cursor[at], { ctx, pal } = p;
      ctx.strokeStyle = pal.ink3; ctx.lineWidth = 1.25; ctx.beginPath(); ctx.arc(gx + 4, cy + line / 2, 3, 0, Math.PI * 2); ctx.stroke();
      lines.forEach((text, index) => p.text(text, gx + 13, cy + line / 2 + index * line, font, pal.ink2));
      cursor[at] += lines.length * line + 4;
      drawn++;
    }
    return drawn;
  }

  /** Container, service, external, backup target or storage strip. */
  private unit(node: LiveNode): void {
    const r = this.rect(node.rect);
    if (!this.visible(r)) return;
    const { ctx, pal } = this.p, p = this.p, k = this.cam.k, layers = this.state.layers;
    const chip = !!node.parent && (node.parent.kind === 'container' || node.parent.kind === 'service');
    const level = levelOf(node, r.w, r.h, k);
    this.levels.set(node.id, level);
    if (node.kind === 'disk') { this.disk(node, r, level); return; }
    const radius = clamp(7 * k, 2, 7);
    const warned = layers.warnings && node.warnings.length > 0;
    if (node.kind === 'external' || node.kind === 'target') {
      const off = node.kind === 'target' && node.health.tone !== 'success';
      p.rrect(r.x, r.y, r.w, r.h, clamp(8 * k, 3, 8));
      // Backup layer: a target that holds no copy is hatched, like data without a copy.
      if (off && layers.backup) { ctx.fillStyle = p.hatch(pal.backup, 0.4); ctx.fill(); } else { ctx.fillStyle = pal.outside; ctx.fill(); }
      ctx.save(); ctx.setLineDash(node.kind === 'target' ? [5, 4] : [2, 3]); ctx.strokeStyle = off && layers.backup ? pal.backup : pal.f2; ctx.lineWidth = 1.25; ctx.stroke(); ctx.restore();
    } else {
      const machine = isFrame(node);
      if (node.kind === 'service') p.notched(r.x, r.y, r.w, r.h, clamp(11 * k, 4, 12), Math.min(radius, 3)); else p.rrect(r.x, r.y, r.w, r.h, machine ? clamp(12 * k, 3, 12) : radius);
      ctx.fillStyle = machine ? pal.s1 : pal.s3; ctx.fill();
      // Warnings layer: the whole surface takes a quiet tint (never an accent edge).
      if (warned) { ctx.fillStyle = node.warnings.some(w => w.tone === 'danger') ? pal.dangerBg : pal.warnBg; ctx.globalAlpha = 0.45; ctx.fill(); ctx.globalAlpha = 1; }
      ctx.strokeStyle = machine ? pal.f1 : pal.f3; ctx.lineWidth = machine ? 1.5 : 1; ctx.stroke();
    }
    if (r.w < 16 || r.h < 13) return;
    const pad = clamp(r.w * 0.06, 6, 16);
    const x = r.x + pad + (node.kind === 'service' && !chip ? clamp(r.w * 0.02, 0, 4) : 0), w = r.x + r.w - pad - x;
    const chips = chip ? 0 : node.children.length ? (r.y + r.h) - this.cam.sy(Math.min(...node.children.map(child => child.rect.y))) + 4 : 0;
    const bottom = r.y + r.h - (chip ? 4 : clamp(pad * 0.6, 4, 12)) - chips;
    const ts = chip ? clamp(10.5 + r.w * 0.012, 11.5, 14) : clamp(10 + r.w * 0.02, 11.5, 17);
    const titleFont = p.font(600, ts), line = ts + 3;
    // A name that is too long for one line breaks into two; it is never shortened.
    // The corner belongs to the warning pin when the node has one.
    const room = w - 16 - (warned ? 8 : 0);
    const maxLines = chip || r.h < 2 * line + 4 ? 1 : 2;
    const name = p.wrap(node.name, room, titleFont, maxLines) ?? p.wrap(node.short, room, titleFont, maxLines);
    // Performance layer at the far level: a load gauge along the foot of the node, without text.
    const load = node.slots?.total ? node.slots.busy / node.slots.total : node.perf?.cpu ?? null;
    if (level < 2 && !chip && layers.perf && load != null && r.w >= 40 && r.h >= 30 && !chips) this.gauge(r.x + pad, r.y + r.h - 8, r.w - 2 * pad, 3, load);
    const lines = name?.length ?? 1;
    // A service inside a container that has something to say under its name, and room for it, sets the name at the top.
    const below = chip && level >= 3 && r.h >= line + 24
      && (canvasFacts(node, this.scene!, level, this.state.order).some(fact => fact.key !== 'status') || canvasGaps(node, this.scene!, level, this.state.order).length > 0);
    let y = r.y + (chip && !below ? (r.h - lines * line) / 2 : r.h < lines * line + 22 ? Math.max(2, (r.h - lines * line) / 2) : below ? 5 : clamp(pad * 0.8, 5, 14));
    if (y + line > r.y + r.h + (chip ? 2 : 0)) return;
    if (!name) { if (r.w >= 18) p.mark(r.x + r.w / 2, r.y + r.h / 2, node.health.tone); return; }
    p.mark(x + 5, y + line / 2, node.health.tone);
    name.forEach((text, index) => p.text(text, x + 16, y + line / 2 + index * line, titleFont, pal.ink));
    if (chip && level < 2) return;
    const subFont = p.font(400, 11, true);
    let facts = canvasFacts(node, this.scene!, level, this.state.order), shown = 0;
    if (chip) {
      // Its state stands on the line of its name, so even the lowest service box says how it is.
      const state = nodeFacts(node, this.scene!).find(fact => fact.key === 'status' && fact.level <= Math.max(level, 3) && (level >= 3 || fact.observed));
      const value = state ? p.pick(state.values, w - 16 - p.tw(name[0], titleFont) - 14, p.font(500, 11, true)) : null;
      if (state && value) {
        p.text(value, x + w, y + line / 2, p.font(500, 11, true), state.tone === 'danger' ? pal.danger : state.tone === 'warning' ? pal.warn : state.observed ? pal.ink : pal.ink3, 'right');
        facts = facts.filter(fact => fact !== state);
        shown++;
      }
    }
    y += lines * line + 2;
    const role = y + 14 <= bottom && (!chip || r.h >= 88) ? p.pick([node.role, node.role.split(' · ')[0]], w, subFont) : null;
    if (role) { p.text(role, x, y + 7, subFont, node.kind === 'target' && layers.backup && node.health.tone !== 'success' ? pal.backup : pal.ink2); y += 18; }
    if (level < 2) {
      if (node.source?.facts?.sample && y + 14 <= bottom) {
        const facts = canvasFacts(node, this.scene!, 2, this.state.order);
        const headline = facts.find(fact => fact.key === 'slots') ?? facts.find(fact => fact.meter != null || fact.key === 'memory');
        if (headline?.cells) {
          const { busy, total, held } = headline.cells, cell = Math.min(8, Math.floor(w / total) - 1);
          if (cell > 1 && y + cell + 14 <= bottom) {
            for (let index = 0; index < total; index++) {
              ctx.fillStyle = index < busy ? pal.perf : index >= total - (held ?? 0) ? pal.warn : pal.track;
              ctx.fillRect(x + index * (cell + 1), y, cell, cell);
            }
            y += cell + 1;
          }
          const value = p.pick([busy + ' / ' + total + ' slots'], w, subFont);
          if (value) { p.text(value, x, y + 7, subFont, pal.ink); this.shown.set(node.id, 1); }
        } else if (headline && this.factRow(headline, x, y, w, 14)) this.shown.set(node.id, 1);
      }
      return;
    }
    if (level >= 3 && layers.perf && node.slots?.cards.length && node.source?.facts?.sample)
      y = this.cardSlots(node, x, y, w, bottom);
    // The first finding spans the node as a band; everything else is a row.
    const band = facts.find(fact => fact.band);
    if (band && y + 19 <= bottom + 0.5 && this.factRow(band, x, y, w, 19)) { y += 24; shown++; }
    // From the middle of the container level on a node sets its rows in two columns: twice the facts, each in
    // the wording that fits the column, and a label stays close to its value.
    shown += this.flow(facts.filter(fact => !fact.band), canvasGaps(node, this.scene!, level, this.state.order), x, y, w, bottom - y, w >= 330 ? 2 : 1, w >= 520 ? 28 : 20);
    this.shown.set(node.id, shown);
  }

  private cardSlots(node: LiveNode, x: number, y: number, width: number, bottom: number): number {
    const slots = node.slots!;
    const { ctx, pal } = this.p, font = this.p.font(500, 11, true);
    const cellWidth = Math.max(...slots.cards.map(card => this.p.tw(card, font))) + 24;
    const columns = Math.floor(width / cellWidth);
    if (!columns) return y;
    const rows = Math.min(Math.ceil(slots.cards.length / columns), Math.floor((bottom - y - 36) / 20));
    if (rows < 1) return y;
    slots.cards.slice(0, rows * columns).forEach((card, index) => {
      const left = x + index % columns * cellWidth, top = y + Math.floor(index / columns) * 20;
      ctx.fillStyle = pal.track; ctx.fillRect(left, top, cellWidth - 4, 17);
      ctx.fillStyle = pal.perf; ctx.fillRect(left + 4, top + 5, 7, 7);
      this.p.text(card, left + 15, top + 8, font, pal.ink);
      this.cardHits.push({ node: node.id, card, rect: { x: left, y: top, w: cellWidth - 4, h: 17 } });
    });
    return y + rows * 20;
  }

  private gauge(x: number, y: number, w: number, h: number, fraction: number): void {
    const { ctx, pal } = this.p;
    ctx.fillStyle = pal.track; ctx.fillRect(x, y, w, h);
    ctx.fillStyle = pal.perf; ctx.fillRect(x, y, w * clamp(fraction, 0, 1), h);
  }

  /** One fact as a row. Returns false when no wording fits, so the row is left out. */
  private factRow(fact: LiveFact, x: number, y: number, w: number, h: number): boolean {
    const { ctx, pal } = this.p, p = this.p, cy = y + h / 2;
    const valueFont = p.font(500, 11, true), labelFont = p.font(400, 11, true);
    const tone = fact.tone === 'danger' ? pal.danger : fact.tone === 'warning' ? pal.warn : fact.tone === 'success' ? pal.ok : fact.tone === 'muted' ? pal.ink3 : pal.ink;
    if (fact.band) {
      const value = p.pick(fact.values, w - 20, valueFont);
      if (!value) return false;
      p.rrect(x - 3, y, w + 6, h, 3); ctx.fillStyle = fact.tone === 'danger' ? pal.dangerBg : pal.warnBg; ctx.fill();
      p.tri(x + 5, cy, 4.5, tone);
      p.text(value, x + 14, cy, valueFont, tone);
      return true;
    }
    const glyph = fact.layer ? 13 : 0;
    const lw = p.tw(fact.label, labelFont);
    if (fact.cells) {
      const { busy, total } = fact.cells, s = clamp((w - glyph - lw - 12) / total - 2, 5, 11), gap = clamp(s * 0.28, 2, 3);
      const cw = total * (s + gap) - gap;
      const value = p.pick(fact.values, w - glyph - cw - 10, valueFont) ?? p.pick(fact.values, w - glyph - lw - 10, valueFont);
      if (!value) return false;
      this.layerGlyph(fact.layer, x, cy);
      const fits = glyph + cw + 10 + p.tw(value, valueFont) <= w;
      if (fits) for (let i = 0; i < total; i++) { ctx.fillStyle = i < busy ? pal.perf : i >= total - (fact.cells.held ?? 0) ? pal.warn : pal.track; ctx.fillRect(x + glyph + i * (s + gap), cy - s / 2, s, s); }
      else p.text(fact.label, x + glyph, cy, labelFont, pal.ink2);
      p.text(value, x + w, cy, valueFont, tone, 'right');
      return true;
    }
    const room = w - glyph - lw - 10;
    const value = p.pick(fact.values, room, valueFont);
    if (!value) return false;
    this.layerGlyph(fact.layer, x, cy);
    p.text(fact.label, x + glyph, cy, labelFont, pal.ink2);
    const vw = p.tw(value, valueFont);
    if (fact.meter != null) {
      const bx = x + glyph + lw + 8, bw = x + w - vw - 8 - bx;
      if (bw >= 24) {
        const storage = fact.layer === 'storage';
        const bh = storage ? 6 : 3;
        ctx.fillStyle = pal.track; ctx.fillRect(bx, cy - bh / 2, bw, bh);
        ctx.fillStyle = fact.tone ? tone : storage ? pal.storage : pal.perf; ctx.fillRect(bx, cy - bh / 2, bw * clamp(fact.meter, 0, 1), bh);
        // Storage meters carry the pressure mark at 85% used, performance meters are plain.
        if (storage) { ctx.fillStyle = pal.ink2; ctx.fillRect(Math.round(bx + bw * 0.85), cy - bh / 2 - 2, 1, bh + 4); }
      }
    }
    p.text(value, x + w, cy, valueFont, tone, 'right');
    return true;
  }

  /** Each layer marks its rows with its own small sign, so a value can be traced to its layer without colour. */
  private layerGlyph(layer: LiveLayer | null, x: number, cy: number): void {
    if (!layer) return;
    const { ctx, pal } = this.p, color = pal[LAYER_COLOR[layer]] as string;
    ctx.fillStyle = color; ctx.strokeStyle = color; ctx.lineWidth = 1.25;
    if (layer === 'perf') { ctx.fillRect(x, cy + 1, 2, 3); ctx.fillRect(x + 3, cy - 2, 2, 6); ctx.fillRect(x + 6, cy - 4, 2, 8); }
    else if (layer === 'network') { ctx.beginPath(); ctx.moveTo(x, cy); ctx.lineTo(x + 3, cy); ctx.stroke(); ctx.strokeRect(x + 3.5, cy - 2.5, 5, 5); }
    else if (layer === 'storage') { ctx.fillRect(x, cy - 4, 8, 2); ctx.fillRect(x, cy - 1, 8, 2); ctx.fillRect(x, cy + 2, 8, 2); }
    else if (layer === 'backup') { ctx.fillStyle = this.p.hatch(color, 1); ctx.fillRect(x, cy - 4, 8, 8); ctx.strokeRect(x + 0.5, cy - 3.5, 7, 7); }
    else this.p.tri(x + 4, cy, 4, color);
  }

  /** Storage strip: one bar per disk with the pressure mark, data locations as segments, coverage as hatching. */
  private disk(node: LiveNode, r: LiveRect, level: LiveLevel): void {
    const { ctx, pal } = this.p, p = this.p, k = this.cam.k, layers = this.state.layers;
    p.rrect(r.x, r.y, r.w, r.h, clamp(8 * k, 3, 8));
    ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = pal.f3; ctx.lineWidth = 1; ctx.stroke();
    // Backup layer: storage that holds data without a copy gets a dashed second outline.
    if (layers.backup && node.locations.some(item => item.exists && item.coverage === 'none')) {
      ctx.save(); ctx.setLineDash([5, 4]); ctx.strokeStyle = pal.backup; ctx.lineWidth = 1.25;
      p.rrect(r.x - 3, r.y - 3, r.w + 6, r.h + 6, clamp(8 * k, 3, 8) + 3); ctx.stroke(); ctx.restore();
    }
    if (r.w < 40 || r.h < 12) return;
    const padY = clamp(r.h * 0.12, 4, 12), padX = clamp(r.w * 0.03, 6, 16);
    const x = r.x + padX, w = r.w - 2 * padX;
    let y = r.y + padY;
    const bottom = r.y + r.h - padY;
    const worst = [...node.disks].sort((a, b) => diskUsed(b) - diskUsed(a))[0];
    const pressure = worst ? diskPressure(worst) : null;
    let shown = 0;
    if (r.h >= 34 && r.w >= 120) {
      const titleFont = p.font(600, clamp(11 + r.h * 0.03, 12, 14)), valueFont = p.font(500, 11, true);
      p.mark(x + 5, y + 8, node.health.tone);
      p.text(node.name, x + 16, y + 8, titleFont, pal.ink);
      const nameEnd = x + 16 + p.tw(node.name, titleFont) + 10;
      const pinned = layers.warnings && node.warnings.length ? 12 : 0;
      const value = worst ? p.pick([`${percent(diskUsed(worst))} · ${gib(worst.usedBytes)} of ${gib(worst.totalBytes)}`, percent(diskUsed(worst))], x + w - pinned - nameEnd, valueFont)
        : p.pick([node.source?.facts?.device?.sizeBytes != null ? gib(node.source.facts.device.sizeBytes) : null, 'disk use not observed', 'not observed'], x + w - pinned - nameEnd, valueFont);
      if (value) { p.text(value, x + w - pinned, y + 8, valueFont, !worst ? pal.ink3 : pressure && layers.warnings ? (pressure === 'danger' ? pal.danger : pal.warn) : pal.ink, 'right'); shown++; }
      const role = value ? p.pick([node.role], x + w - pinned - p.tw(value, valueFont) - 12 - nameEnd, p.font(400, 11, true)) : null;
      if (role) p.text(role, nameEnd, y + 8.5, p.font(400, 11, true), pal.ink2);
      y += 21;
    }
    // Storage layer: the data locations as segments of the disk that holds them (drawn when there is one disk).
    const present = node.locations.filter(item => item.exists);
    const segmented = layers.storage && node.disks.length === 1 && present.length > 0;
    const count = Math.max(1, node.disks.length);
    const bh = clamp((Math.min(bottom, y + count * 18) - y - (count - 1) * 4) / count, 3, 13);
    const segment = (bytes: number | null | undefined, total: number): number => Math.max(4, ((bytes ?? 0) / total) * w);
    node.disks.forEach((disk, index) => {
      const by = y + index * (bh + 4);
      if (by + bh > bottom + 0.5) return;
      const used = diskUsed(disk) * w, pressed = diskPressure(disk);
      ctx.fillStyle = pal.track; ctx.fillRect(x, by, w, bh);
      ctx.fillStyle = layers.storage ? pal.storage : pal.ink2; ctx.globalAlpha = layers.storage ? 0.35 : 0.5; ctx.fillRect(x, by, used, bh); ctx.globalAlpha = 1;
      if (segmented) {
        let sx = x;
        for (const location of present) { const sw = segment(location.sizeBytes, disk.totalBytes); ctx.fillStyle = pal.storage; ctx.fillRect(sx, by, sw - 1, bh); sx += sw; }
      }
      if (pressed && layers.warnings && !layers.storage) { ctx.fillStyle = pressed === 'danger' ? pal.danger : pal.warn; ctx.fillRect(x + w * 0.85, by, Math.max(0, used - w * 0.85), bh); }
      // Pressure mark: the Studio warns when less than 15% is free.
      const tx = Math.round(x + w * 0.85) + 0.5;
      ctx.strokeStyle = pressed && layers.warnings ? pal.warn : pal.ink2; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(tx, by - 3); ctx.lineTo(tx, by + bh + 3); ctx.stroke();
    });
    y += node.disks.length ? count * (bh + 4) + 3 : 0;
    const font = p.font(400, 11, true);
    // Backup layer: one block for each data location that must be kept. Hatched and dashed means no copy
    // off the host, a dotted outline a git remote only, a solid outline with a check a backup.
    if (layers.backup && present.length && bottom - y >= 6) {
      const rowH = clamp(bottom - y, 6, 20), gap = 4, bw = (w - (present.length - 1) * gap) / present.length;
      present.forEach((location, index) => {
        const bx = x + index * (bw + gap), covered = location.coverage === 'backup' || location.coverage === 'both', remote = location.coverage === 'git-remote';
        p.rrect(bx, y, bw, rowH, 3);
        ctx.save();
        if (covered) { ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = pal.ok; ctx.lineWidth = 1.5; ctx.stroke(); }
        else if (remote) { ctx.fillStyle = pal.s3; ctx.fill(); ctx.setLineDash([1.5, 2.5]); ctx.strokeStyle = pal.ink2; ctx.lineWidth = 1.25; ctx.stroke(); }
        else { ctx.fillStyle = p.hatch(pal.backup, 0.6); ctx.fill(); ctx.setLineDash([4, 3]); ctx.strokeStyle = pal.backup; ctx.lineWidth = 1.25; ctx.stroke(); }
        ctx.restore();
        if (rowH < 16) return;
        const inset = covered ? 20 : 7, word = COVERAGE_WORD[location.coverage] ?? location.coverage;
        const label = p.pick([`${location.label} · ${word}`, location.label, word], bw - inset - 7, font);
        if (!label) return;
        if (covered) p.mark(bx + 10, y + rowH / 2, 'success');
        // Text on hatching gets a plain ground, so it stays readable.
        if (!covered && !remote) { ctx.fillStyle = pal.s3; ctx.fillRect(bx + inset - 3, y + 3, p.tw(label, font) + 6, rowH - 6); }
        p.text(label, bx + inset, y + rowH / 2, font, remote ? pal.ink2 : pal.ink);
      });
      y += rowH + 5;
    }
    if (segmented && y + 14 <= bottom) {
      // The segments in their order, named with their size. One that does not fit ends the row; none is cut.
      const disk = node.disks[0], free = `free ${gib(disk.freeBytes)}`;
      let sx = x;
      const limit = x + w - p.tw(free, font) - 16;
      for (const location of present) {
        const label = `${location.label} ${location.sizeBytes != null ? `${location.sizeComplete ? '' : 'at least '}${size(location.sizeBytes)}` : 'not measured'}`;
        const lw = p.tw(label, font) + 12;
        if (sx + lw > limit) break;
        ctx.fillStyle = pal.storage; ctx.fillRect(sx, y + 3, 7, 7);
        p.text(label, sx + 12, y + 7, font, pal.ink2);
        sx += lw + 14;
      }
      if (limit > x) p.text(free, x + w, y + 7, font, pal.ink2, 'right');
      y += 18;
    }
    // Remaining room: the facts of the active layers, in columns. The strip already says the fullest disk.
    if (level >= 2) shown += this.flow(canvasFacts(node, this.scene!, level, this.state.order).filter(fact => fact.key !== 'disk'),
      canvasGaps(node, this.scene!, level, this.state.order), x, y, w, bottom - y, Math.max(1, Math.floor((w + 20) / 300)), 20);
    this.shown.set(node.id, shown);
  }

  private points(route: LiveRoute): [number, number][] { return route.points.map(q => [Math.round(this.cam.sx(q[0])) + 0.5, Math.round(this.cam.sy(q[1])) + 0.5]); }
  private linkColor(link: LiveLink): string {
    const { pal } = this.p, layers = this.state.layers;
    if (link.layer === 'backup') return layers.backup ? (link.health.tone === 'success' ? pal.ok : pal.backup) : pal.ink3;
    if (!layers.network) return pal.ink3;
    return link.health.tone === 'danger' ? pal.danger : link.health.tone === 'neutral' ? pal.ink2 : pal.net;
  }

  /** The line itself. Drawn before the units; routes keep to the gutters, so they rarely pass behind one. */
  private linkStroke(route: LiveRoute): void {
    const { ctx } = this.p, p = this.p, link = route.link, layers = this.state.layers;
    const pts = this.points(route), color = this.linkColor(link);
    const on = link.layer === 'backup' ? layers.backup : layers.network;
    const selected = this.state.selectedLink === link.id, hovered = this.state.hoverLink === link.id;
    ctx.save(); ctx.lineJoin = 'miter'; ctx.lineCap = 'butt'; ctx.strokeStyle = color;
    if (selected || hovered) { p.trace(pts); ctx.globalAlpha = selected ? 0.3 : 0.16; ctx.lineWidth = 9; ctx.stroke(); ctx.globalAlpha = 1; }
    if (!on) { if (link.layer === 'backup') ctx.setLineDash([2, 5]); p.trace(pts); ctx.lineWidth = 1; ctx.stroke(); ctx.restore(); return; }
    // Not observed routes stay dashed: declared, and nobody measured them.
    if (link.health.tone === 'neutral' && link.layer === 'network') ctx.setLineDash([6, 4]);
    if (link.style === 'tunnel') {
      p.trace(pts); ctx.globalAlpha = 0.14; ctx.lineWidth = 7; ctx.stroke(); ctx.globalAlpha = 1;
      ctx.lineWidth = 1.1; p.trace(pts, 3.5); ctx.stroke(); p.trace(pts, -3.5); ctx.stroke();
    } else if (link.style === 'backup') { ctx.setLineDash([2, 5]); ctx.lineWidth = 1.75; p.trace(pts); ctx.stroke(); }
    else { ctx.lineWidth = link.style === 'call' ? 1.75 : 1.25; p.trace(pts); ctx.stroke(); }
    ctx.restore();
  }

  /** Ports where a route crosses a frame, its direction, and a break where it fails. */
  private linkMarks(route: LiveRoute): void {
    const { ctx, pal } = this.p, link = route.link, layers = this.state.layers, k = this.cam.k;
    if (!(link.layer === 'backup' ? layers.backup : layers.network)) return;
    const pts = this.points(route), color = this.linkColor(link);
    for (const crossing of route.crossings) {
      const big = crossing.frame.kind === 'boundary', s = big ? 4.5 : 3;
      if (crossing.frame.kind === 'group' || k * (big ? 3 : 1) < 0.42) continue;
      const cx = Math.round(this.cam.sx(crossing.x)) + 0.5, cy = Math.round(this.cam.sy(crossing.y)) + 0.5;
      ctx.fillStyle = big ? color : pal.s3; ctx.strokeStyle = color; ctx.lineWidth = 1.25;
      ctx.beginPath(); ctx.rect(cx - s, cy - s, s * 2, s * 2); ctx.fill(); ctx.stroke();
    }
    const a = pts[pts.length - 2], b = pts[pts.length - 1];
    const len = Math.hypot(b[0] - a[0], b[1] - a[1]) || 1, dx = (b[0] - a[0]) / len, dy = (b[1] - a[1]) / len;
    if (link.arrow && k > 0.3) {
      const s = 5.5;
      ctx.beginPath(); ctx.moveTo(b[0], b[1]); ctx.lineTo(b[0] - dx * s * 1.7 - dy * s, b[1] - dy * s * 1.7 + dx * s); ctx.lineTo(b[0] - dx * s * 1.7 + dy * s, b[1] - dy * s * 1.7 - dx * s); ctx.closePath();
      ctx.fillStyle = color; ctx.fill();
    }
    if (link.broken || link.health.tone === 'danger') {
      // A gap with a cross: nothing passes here.
      const mid = pts.length > 2 ? pts[1] : [(pts[0][0] + b[0]) / 2, (pts[0][1] + b[1]) / 2];
      ctx.fillStyle = pal.inside; ctx.fillRect(mid[0] - 7, mid[1] - 7, 14, 14);
      ctx.strokeStyle = color; ctx.lineWidth = 1.75; ctx.beginPath(); ctx.moveTo(mid[0] - 4, mid[1] - 4); ctx.lineTo(mid[0] + 4, mid[1] + 4); ctx.moveTo(mid[0] + 4, mid[1] - 4); ctx.lineTo(mid[0] - 4, mid[1] + 4); ctx.stroke();
    }
  }

  /** The label sits on the longest run that crosses no node; a wording that does not fit there is left out. */
  private linkLabel(route: LiveRoute): void {
    const { ctx, pal } = this.p, p = this.p, link = route.link, layers = this.state.layers;
    if (!(link.layer === 'backup' ? layers.backup : layers.network)) return;
    const best = this.freeRun(route), color = this.linkColor(link);
    if (!best) return;
    const font = p.font(500, 11, true);
    const latency = link.latencyMs != null ? `${link.latencyMs.toFixed(1)} ms` : null;
    const state = link.health.word.toLowerCase();
    const label = p.pick([[link.short, latency ?? state].join(' · '), latency ?? link.short, link.short], best.len - 20, font);
    if (!label) return;
    const w = Math.round(p.tw(label, font) + 12), h = 17;
    const box = best.horizontal ? { x: best.x - w / 2, y: best.y - h / 2, w, h } : { x: best.x - h / 2, y: best.y - w / 2, w: h, h: w };
    if (this.labels.some(other => box.x < other.x + other.w + 6 && other.x < box.x + box.w + 6 && box.y < other.y + other.h + 4 && other.y < box.y + box.h + 4)) return;
    this.labels.push(box);
    ctx.save(); ctx.translate(Math.round(best.x) + 0.5, Math.round(best.y) + 0.5); if (!best.horizontal) ctx.rotate(-Math.PI / 2);
    p.rrect(-Math.round(w / 2), -h / 2, w, h, 3);
    ctx.fillStyle = pal.s3; ctx.fill(); ctx.strokeStyle = color; ctx.globalAlpha = 0.6; ctx.lineWidth = 1; ctx.stroke(); ctx.globalAlpha = 1;
    p.text(label, 0, 0, font, color, 'center');
    ctx.restore();
  }

  /** Longest stretch of a route, in screen pixels, that lies over no container, service, storage strip or external. */
  private freeRun(route: LiveRoute): { len: number; x: number; y: number; horizontal: boolean } | null {
    // Nodes block a label, and so does the header strip of a frame.
    const blocks = this.scene!.order.filter(node => node.kind !== 'boundary')
      .map(node => isFrame(node) && node.children.length ? { x: node.rect.x, y: node.rect.y, w: node.rect.w, h: node.headH } : node.rect);
    let best: { len: number; x: number; y: number; horizontal: boolean } | null = null;
    for (let i = 1; i < route.points.length; i++) {
      const a = route.points[i - 1], b = route.points[i], horizontal = a[1] === b[1];
      const from = Math.min(horizontal ? a[0] : a[1], horizontal ? b[0] : b[1]), to = Math.max(horizontal ? a[0] : a[1], horizontal ? b[0] : b[1]);
      const fixed = horizontal ? a[1] : a[0];
      const cuts = blocks.filter(r => horizontal ? fixed > r.y - 6 && fixed < r.y + r.h + 6 : fixed > r.x - 6 && fixed < r.x + r.w + 6)
        .map(r => horizontal ? [r.x - 4, r.x + r.w + 4] : [r.y - 4, r.y + r.h + 4]).sort((p, q) => p[0] - q[0]);
      let cursor = from;
      const consider = (start: number, end: number): void => {
        const len = (end - start) * this.cam.k;
        if (len > (best?.len ?? 0)) {
          const mid = (start + end) / 2;
          best = { len, x: this.cam.sx(horizontal ? mid : fixed), y: this.cam.sy(horizontal ? fixed : mid), horizontal };
        }
      };
      for (const [start, end] of cuts) { if (start > cursor) consider(cursor, Math.min(start, to)); cursor = Math.max(cursor, end); if (cursor >= to) break; }
      if (cursor < to) consider(cursor, to);
    }
    return best;
  }

  /** Warnings layer: a pin on the corner of every node that has a finding. */
  private pin(node: LiveNode): void {
    if (!node.warnings.length || node.kind === 'boundary') return;
    const r = this.rect(node.rect);
    if (!this.visible(r) || r.w < 14) return;
    const { ctx, pal } = this.p, danger = node.warnings.some(w => w.tone === 'danger');
    const cx = r.x + r.w - clamp(r.w * 0.04, 1, 4), cy = r.y + clamp(r.w * 0.04, 1, 4);
    ctx.beginPath(); ctx.arc(cx, cy, 10.5, 0, Math.PI * 2); ctx.fillStyle = pal.s3; ctx.fill();
    this.p.tri(cx, cy + 0.5, 8, danger ? pal.danger : pal.warn);
    ctx.fillStyle = pal.onTone; ctx.fillRect(cx - 0.9, cy - 3.5, 1.8, 5.2); ctx.fillRect(cx - 0.9, cy + 3, 1.8, 1.8);
  }

  private rings(): void {
    const { ctx, pal } = this.p, scene = this.scene!;
    const ring = (id: string | null, strong: boolean): void => {
      const node = id ? scene.byId.get(id) : null;
      if (!node || node.kind === 'boundary') return;
      const r = this.rect(node.rect), o = strong ? 4 : 3;
      this.p.rrect(r.x - o, r.y - o, r.w + 2 * o, r.h + 2 * o, clamp(10 * this.cam.k, 4, 12) + o);
      ctx.strokeStyle = pal.ink; ctx.lineWidth = strong ? 2 : 1; ctx.globalAlpha = strong ? 1 : 0.5; ctx.stroke(); ctx.globalAlpha = 1;
    };
    if (this.state.hover && this.state.hover !== this.state.selected) ring(this.state.hover, false);
    ring(this.state.selected, true);
  }

  /** What is under a screen point: a route within reach first, else the innermost node. */
  pick(sx: number, sy: number, state: LiveRenderState): LiveHit | null {
    const scene = this.scene, layout = this.layout;
    if (!scene || !layout) return null;
    const slot = this.cardHits.find(({ rect }) => sx >= rect.x && sx <= rect.x + rect.w && sy >= rect.y && sy <= rect.y + rect.h);
    if (slot) return { node: slot.node, card: slot.card };
    let best: LiveRoute | null = null, distance = 8;
    for (const route of layout.routes) {
      if (!(route.link.layer === 'backup' ? state.layers.backup : state.layers.network)) continue;
      const pts = this.points(route);
      for (let i = 1; i < pts.length; i++) {
        const [ax, ay] = pts[i - 1], [bx, by] = pts[i];
        const len2 = (bx - ax) ** 2 + (by - ay) ** 2 || 1;
        const t = clamp(((sx - ax) * (bx - ax) + (sy - ay) * (by - ay)) / len2, 0, 1);
        const d = Math.hypot(sx - (ax + (bx - ax) * t), sy - (ay + (by - ay) * t));
        if (d < distance) { distance = d; best = route; }
      }
    }
    const wx = this.cam.wx(sx), wy = this.cam.wy(sy);
    const inside = (node: LiveNode): boolean => wx >= node.rect.x && wx <= node.rect.x + node.rect.w && wy >= node.rect.y && wy <= node.rect.y + node.rect.h;
    let hit: LiveNode | null = null;
    const walk = (nodes: LiveNode[]): void => { for (const node of nodes) if (inside(node)) { if (node.kind !== 'boundary') hit = node; walk(node.children); } };
    walk([scene.boundary, ...scene.outside]);
    const found = hit as LiveNode | null;
    // A route wins only over the body of a frame: a unit, or the header strip of a frame, is the more likely target.
    const header = !!found && isFrame(found) && wy <= found.rect.y + found.headH && distance > 3;
    if (best && (!found || (isFrame(found) && !header))) return { link: best.link.id };
    return found ? { node: found.id } : null;
  }
}

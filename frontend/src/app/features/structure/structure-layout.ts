import { isGroup, isPlacement, kindText, observationText, observationWord, summaryText,
  type StructureBox, type StructureData, type StructureNode } from './structure.model';

export type Point = readonly [number, number];
export interface Rect { x: number; y: number; w: number; h: number; }
export interface LayoutBox extends Rect {
  id: string;
  box: StructureBox;
  parentId: string | null;
  depth: number;
  /** A leaf instance or a collapsed group: links never cross it. */
  opaque: boolean;
}
export interface RoutedLink {
  number: number;
  kind: string;
  label: string;
  count: number;
  from: string;
  to: string;
  fromLabel: string;
  toLabel: string;
  points: Point[];
  path: string;
  keyX: number;
  keyY: number;
}
export interface StructureLayout {
  boxes: LayoutBox[];
  links: RoutedLink[];
  width: number;
  height: number;
  /** Column or row that holds externals outside every boundary. */
  outside: Rect | null;
}

/** Spacing in px; gutters are wide enough for a link to pass between two boxes. */
export const LAYOUT = { pad: 10, gap: 10, rootGap: 16, header: 32, leaf: 44, opaque: 46, summary: 62, outsideLabel: 24, margin: 12 };
const MIN_SIDE_COLUMN_MAIN = 480;
const textWidth = (text: string, size: number, bold = false): number =>
  Math.ceil(text.length * size * (bold ? 0.62 : 0.56));
const clamp = (value: number, min: number, max: number): number => Math.max(min, Math.min(max, value));

interface Measured { box: StructureBox; w: number; h: number; x: number; y: number; opaque: boolean; children: Measured[]; }

/**
 * Second text line of an instance: observation, placements or owner. The
 * container view drops placement boxes, so it names the environment instead.
 */
export function leafDetail(box: StructureBox, view: 'host' | 'container' = 'host'): string {
  const observed = observationText(box);
  const detail = observed ? `declared · ${observed}` : box.placements > 1 ? `${box.placements} placements`
    : box.otherProject ? `other project · ${box.ownerLabel ?? box.project}` : kindText(box);
  return view === 'container' && box.environmentId ? `${box.environmentId} · ${detail}` : detail;
}

function measure(box: StructureBox, avail: number, view: 'host' | 'container'): Measured {
  const { pad, gap, header } = LAYOUT;
  if (box.children.length === 0 && !isGroup(box)) {
    const replicas = box.replicas > 1 ? ` ×${box.replicas}` : '';
    if (!box.observation) {
      const w = clamp(Math.max(textWidth(box.label + replicas, 12, true) + 30,
        textWidth(leafDetail(box, view), 10) + 40), 128, 240);
      return { box, w: Math.min(w, avail), h: LAYOUT.leaf, x: 0, y: 0, opaque: true, children: [] };
    }
    const version = box.observation?.version
      ? `${box.observation.behind ? '↓ behind · ' : ''}${box.observation.version}${box.observation.expectedVersion ? ` · expected ${box.observation.expectedVersion}` : ''}`
      : '';
    const w = clamp(Math.max(textWidth(box.label + replicas, 12, true) + textWidth(observationWord(box.observation), 10) + 42,
      textWidth(leafDetail(box, view), 10) + 40, textWidth(version, 10) + 36), 240, 320);
    return { box, w: Math.min(w, avail), h: box.observation?.difference === 'undeclared' ? 72 : 60,
      x: 0, y: 0, opaque: true, children: [] };
  }
  if (box.collapsed || box.children.length === 0) {
    const summary = box.children.length ? summaryText(box) : '';
    const w = clamp(Math.max(textWidth(box.label, 12, true) + 48, textWidth(kindText(box), 10) + 32,
      textWidth(summary, 11) + 32,
      box.observation ? textWidth(box.label, 12, true) + textWidth(observationWord(box.observation), 10) + 42 : 0),
      box.observation ? 220 : 160, box.observation ? 320 : 280);
    return { box, w: Math.min(w, avail), h: box.observation ? summary ? 72 : 60 : summary ? LAYOUT.summary : LAYOUT.opaque,
      x: 0, y: 0, opaque: true, children: [] };
  }
  const inner = Math.max(avail - pad * 2, 120);
  const children = box.children.map(child => measure(child, inner, view));
  let x = 0, y = header, rowHeight = 0, contentWidth = 0;
  for (const child of children) {
    if (x > 0 && x + child.w > inner) { y += rowHeight + gap; x = 0; rowHeight = 0; }
    child.x = pad + x;
    child.y = y;
    x += child.w + gap;
    rowHeight = Math.max(rowHeight, child.h);
    contentWidth = Math.max(contentWidth, x - gap);
  }
  const headerWidth = textWidth(box.label, 12, true) + textWidth(kindText(box), 10) +
    (box.observation ? textWidth(observationWord(box.observation), 10) : 0) + 56;
  return { box, w: Math.max(contentWidth + pad * 2, Math.min(headerWidth, avail)), h: y + rowHeight + pad,
    x: 0, y: 0, opaque: false, children };
}

/**
 * Deterministic nested flow layout and link routing for one projection. The
 * same roots, links and width give the same picture. Externals without a
 * parent sit in an "Outside the boundary" column, or a row when the width is
 * too small for a column.
 */
export function layoutStructure(roots: readonly StructureBox[], data: StructureData,
  view: 'host' | 'container', width: number): StructureLayout {
  const { gap, rootGap, outsideLabel, margin } = LAYOUT;
  const available = width - margin * 2;
  const isOutside = (box: StructureBox) => box.kind === 'external' && box.children.length === 0;
  const externals = roots.filter(isOutside).map(box => measure(box, 240, view));
  const outsideWidth = Math.max(0, ...externals.map(item => item.w));
  const sideColumn = externals.length > 0 && available - outsideWidth - rootGap >= MIN_SIDE_COLUMN_MAIN;
  const mainWidth = sideColumn ? available - outsideWidth - rootGap : available;
  const main = roots.filter(box => !isOutside(box)).map(box => measure(box, mainWidth, view));
  let x = 0, y = 0, rowHeight = 0, contentWidth = 0;
  for (const item of main) {
    if (x > 0 && x + item.w > mainWidth) { y += rowHeight + rootGap; x = 0; rowHeight = 0; }
    item.x = x; item.y = y;
    x += item.w + rootGap;
    rowHeight = Math.max(rowHeight, item.h);
    contentWidth = Math.max(contentWidth, x - rootGap);
  }
  let height = y + rowHeight;
  let outside: Rect | null = null;
  if (externals.length) {
    const left = sideColumn ? (main.length ? contentWidth + rootGap : 0) : 0;
    const top = sideColumn ? 0 : (main.length ? height + rootGap : 0);
    let ox = 0, oy = outsideLabel, rowH = 0, outsideW = 0;
    for (const item of externals) {
      if (!sideColumn && ox > 0 && ox + item.w > available) { oy += rowH + gap; ox = 0; rowH = 0; }
      item.x = left + ox; item.y = top + oy;
      if (sideColumn) oy += item.h + gap;
      else { ox += item.w + gap; rowH = Math.max(rowH, item.h); }
      outsideW = Math.max(outsideW, sideColumn ? item.w : ox - gap);
    }
    const outsideH = sideColumn ? oy - gap : oy + rowH;
    outside = { x: left + margin, y: top + margin, w: Math.max(outsideW, 160), h: outsideH };
    contentWidth = Math.max(contentWidth, left + outside.w);
    height = Math.max(height, top + outsideH);
  }

  const boxes: LayoutBox[] = [];
  const place = (item: Measured, ox: number, oy: number, parentId: string | null, depth: number): void => {
    const placed: LayoutBox = { id: item.box.id, box: item.box, parentId, depth, opaque: item.opaque,
      x: ox + item.x, y: oy + item.y, w: item.w, h: item.h };
    boxes.push(placed);
    for (const child of item.children) place(child, placed.x, placed.y, placed.id, depth + 1);
  };
  // The margin keeps routes that pass above or beside the outermost boxes inside the drawing.
  for (const item of [...main, ...externals]) place(item, margin, margin, null, 0);
  const label: Rect[] = outside ? [{ x: outside.x, y: outside.y, w: outside.w, h: outsideLabel }] : [];
  return { boxes, links: routeLinks(boxes, roots, data, view, label), width: Math.ceil(contentWidth) + margin * 2,
    height: Math.ceil(height) + margin * 2, outside };
}

/**
 * Attaches each link to the nearest visible placement box (host view) or
 * function group (container view), merges duplicates, numbers them for the key
 * and routes them through the gutters.
 */
function routeLinks(boxes: LayoutBox[], roots: readonly StructureBox[], data: StructureData,
  view: 'host' | 'container', avoid: readonly Rect[]): RoutedLink[] {
  const byId = new Map(boxes.map(box => [box.id, box]));
  // Every projected id, including members and boxes hidden in a collapsed group, resolves to its visible box.
  const owner = new Map<string, LayoutBox>();
  const own = (box: StructureBox, visible: LayoutBox | undefined): void => {
    const self = byId.get(box.id) ?? visible;
    if (self) for (const id of box.members) owner.set(id, self);
    for (const child of box.children) own(child, self);
  };
  roots.forEach(root => own(root, undefined));
  const source = new Map(data.nodes.map(node => [node.id, node]));
  const parentOf = (box: LayoutBox) => box.parentId ? byId.get(box.parentId) : undefined;
  const attach = (id: string): LayoutBox | undefined => {
    let start = owner.get(id);
    if (!start) {
      // A placement absent from the container view: attach through the first instance it holds.
      const inside = data.nodes.find(node => owner.has(node.id) && isBelow(node, id, source));
      start = inside ? owner.get(inside.id) : undefined;
    }
    for (let box = start; box; box = parentOf(box))
      if (view === 'host' ? isPlacement(box.box) : box.box.kind === 'group') return box;
    return start;
  };
  const isAncestor = (upper: LayoutBox, lower: LayoutBox): boolean => {
    for (let box = parentOf(lower); box; box = parentOf(box)) if (box === upper) return true;
    return false;
  };
  const merged = new Map<string, { kind: string; label: string; a: LayoutBox; b: LayoutBox; count: number }>();
  for (const link of data.links) {
    const a = attach(link.from), b = attach(link.to);
    if (!a || !b || a === b || isAncestor(a, b) || isAncestor(b, a)) continue;
    const key = `${a.id}>${b.id}>${link.kind}`;
    const existing = merged.get(key);
    if (existing) existing.count += 1;
    else merged.set(key, { kind: link.kind, label: link.label || link.kind, a, b, count: 1 });
  }
  const chain = (box: LayoutBox): LayoutBox[] => {
    const out: LayoutBox[] = [];
    for (let current: LayoutBox | undefined = box; current; current = parentOf(current)) out.unshift(current);
    return out;
  };
  const used: Segment[] = [];
  const keys: Rect[] = [...avoid];
  const routed: RoutedLink[] = [];
  for (const { kind, label, a, b, count } of merged.values()) {
    const points = route(a, b, chain(a), chain(b), boxes, used);
    for (let i = 1; i < points.length; i++) used.push([points[i - 1], points[i]]);
    const [keyX, keyY] = placeKey(points, boxes, keys);
    keys.push({ x: keyX - 10, y: keyY - 10, w: 20, h: 20 });
    routed.push({ number: routed.length + 1, kind, label, count, from: a.id, to: b.id,
      fromLabel: a.box.label, toLabel: b.box.label, points,
      path: 'M' + points.map(point => `${Math.round(point[0])} ${Math.round(point[1])}`).join(' L'), keyX, keyY });
  }
  return routed;
}

function isBelow(node: StructureNode, ancestorId: string, source: ReadonlyMap<string, StructureNode>): boolean {
  for (let parent = node.parentId, depth = 0; parent && depth < 64; parent = source.get(parent)?.parentId ?? null, depth++)
    if (parent === ancestorId) return true;
  return false;
}

type Segment = readonly [Point, Point];
const OBSTACLE = 1000;

/** True when the segment enters the interior of the rectangle; touching an edge is allowed. */
export function segmentHits([p, q]: Segment, r: Rect): boolean {
  const left = Math.min(p[0], q[0]), right = Math.max(p[0], q[0]);
  const top = Math.min(p[1], q[1]), bottom = Math.max(p[1], q[1]);
  return right > r.x + 1 && left < r.x + r.w - 1 && bottom > r.y + 1 && top < r.y + r.h - 1;
}

function overlaps([p, q]: Segment, [u, v]: Segment): boolean {
  const horizontal = Math.abs(p[1] - q[1]) < 0.5, otherHorizontal = Math.abs(u[1] - v[1]) < 0.5;
  if (horizontal && otherHorizontal && Math.abs(p[1] - u[1]) < 4)
    return Math.max(p[0], q[0]) > Math.min(u[0], v[0]) && Math.min(p[0], q[0]) < Math.max(u[0], v[0]);
  if (!horizontal && !otherHorizontal && Math.abs(p[0] - u[0]) < 4)
    return Math.max(p[1], q[1]) > Math.min(u[1], v[1]) && Math.min(p[1], q[1]) < Math.max(u[1], v[1]);
  return false;
}

function simplify(points: Point[]): Point[] {
  const out: Point[] = [];
  for (const point of points) {
    const last = out[out.length - 1];
    if (last && Math.abs(last[0] - point[0]) < 0.5 && Math.abs(last[1] - point[1]) < 0.5) continue;
    const before = out[out.length - 2];
    if (last && before && (Math.abs(before[0] - last[0]) < 0.5 && Math.abs(last[0] - point[0]) < 0.5 ||
      Math.abs(before[1] - last[1]) < 0.5 && Math.abs(last[1] - point[1]) < 0.5)) out[out.length - 1] = point;
    else out.push(point);
  }
  return out;
}

/**
 * Orthogonal candidates through the gutter between the two sides, around
 * them, and approaching the target from above or below through its own
 * ancestors. The cheapest candidate wins; a candidate that would cross an
 * instance box falls back to a gutter grid search.
 */
function route(a: LayoutBox, b: LayoutBox, chainA: LayoutBox[], chainB: LayoutBox[],
  boxes: readonly LayoutBox[], used: readonly Segment[]): Point[] {
  let i = 0;
  while (i < chainA.length && i < chainB.length && chainA[i] === chainB[i]) i++;
  const A = chainA[i] ?? a, B = chainB[i] ?? b;
  const ancestors = new Set([...chainA, ...chainB].filter(box => box !== a && box !== b));
  const right = (r: Rect) => r.x + r.w, bottom = (r: Rect) => r.y + r.h;
  const midY = (r: Rect, other: Rect) => clamp((other.y + bottom(other)) / 2, r.y + 14, bottom(r) - 14);
  const midX = (r: Rect, other: Rect) => clamp((other.x + right(other)) / 2, r.x + 16, right(r) - 16);
  const candidates: Point[][] = [];
  if (B.x >= right(A) || A.x >= right(B)) {
    const toRight = B.x >= right(A);
    const x1 = toRight ? right(a) : a.x, x2 = toRight ? b.x : right(b);
    const gx = toRight ? (right(A) + B.x) / 2 : (A.x + right(B)) / 2;
    const y1 = midY(a, b), y2 = midY(b, a);
    for (const d of [0, 6, -6]) candidates.push([[x1, y1], [gx + d, y1], [gx + d, y2], [x2, y2]]);
    const exit = toRight ? right(A) + 5 : A.x - 5, entry = toRight ? B.x - 5 : right(B) + 5;
    const top = Math.min(A.y, B.y) - 5, low = Math.max(bottom(A), bottom(B)) + 5;
    const bx = midX(b, a), ax = midX(a, b);
    candidates.push([[x1, y1], [exit, y1], [exit, top], [entry, top], [entry, y2], [x2, y2]]);
    candidates.push([[x1, y1], [exit, y1], [exit, low], [entry, low], [entry, y2], [x2, y2]]);
    candidates.push([[x1, y1], [exit, y1], [exit, top], [bx, top], [bx, b.y]]);
    candidates.push([[x1, y1], [exit, y1], [exit, low], [bx, low], [bx, bottom(b)]]);
    candidates.push([[ax, a.y], [ax, top], [bx, top], [bx, b.y]]);
    candidates.push([[ax, a.y], [ax, top], [entry, top], [entry, y2], [x2, y2]]);
    candidates.push([[ax, bottom(a)], [ax, low], [entry, low], [entry, y2], [x2, y2]]);
  }
  if (B.y >= bottom(A) || A.y >= bottom(B)) {
    const down = B.y >= bottom(A);
    const y1 = down ? bottom(a) : a.y, y2 = down ? b.y : bottom(b);
    const gy = down ? (bottom(A) + B.y) / 2 : (A.y + bottom(B)) / 2;
    const x1 = midX(a, b), x2 = midX(b, a);
    for (const d of [0, 6, -6]) candidates.push([[x1, y1], [x1, gy + d], [x2, gy + d], [x2, y2]]);
    const exit = down ? bottom(A) + 5 : A.y - 5, entry = down ? B.y - 5 : bottom(B) + 5;
    const side = Math.max(right(A), right(B)) + 5, otherSide = Math.min(A.x, B.x) - 5;
    candidates.push([[x1, y1], [x1, exit], [side, exit], [side, entry], [x2, entry], [x2, y2]]);
    candidates.push([[x1, y1], [x1, exit], [otherSide, exit], [otherSide, entry], [x2, entry], [x2, y2]]);
  }
  if (!candidates.length) candidates.push([[a.x + a.w / 2, a.y], [a.x + a.w / 2, Math.min(a.y, b.y) - 5],
    [b.x + b.w / 2, Math.min(a.y, b.y) - 5], [b.x + b.w / 2, b.y]]);
  let best: { points: Point[]; cost: number } | null = null;
  for (const points of candidates) {
    let cost = points.length * 0.8;
    for (let k = 1; k < points.length; k++) {
      const segment: Segment = [points[k - 1], points[k]];
      for (const box of boxes) {
        if (ancestors.has(box) || !segmentHits(segment, box)) continue;
        cost += box.opaque ? OBSTACLE : box === a || box === b ? 40 : 3;
      }
      for (const other of used) if (overlaps(segment, other)) cost += 1.5;
      cost += (Math.abs(segment[0][0] - segment[1][0]) + Math.abs(segment[0][1] - segment[1][1])) / 600;
    }
    if (!best || cost < best.cost) best = { points, cost };
  }
  if (best!.cost >= OBSTACLE) {
    const common = i > 0 ? chainA[i - 1] : null;
    const margin = LAYOUT.gap;
    const fallback = gridRoute(a, b, boxes, common && common !== a && common !== b
      ? { x: common.x - margin, y: common.y - margin, w: common.w + margin * 2, h: common.h + margin * 2 } : null);
    if (fallback) return fallback;
  }
  return simplify(best!.points);
}

/** Scratch buffers reused by every grid search, grown on demand. */
const scratch = { size: 0, dist: new Float64Array(0), previous: new Int32Array(0),
  blockedH: new Uint8Array(0), blockedV: new Uint8Array(0) };

/**
 * Dijkstra over the gutter grid inside the closest common ancestor: lines run
 * 5 px outside every box edge, so a path never enters an instance box. Bends
 * cost extra so routes stay calm.
 */
function gridRoute(a: LayoutBox, b: LayoutBox, all: readonly LayoutBox[], region: Rect | null): Point[] | null {
  const margin = LAYOUT.gap / 2;
  const inside = (box: Rect) => !region || box.x < region.x + region.w && box.x + box.w > region.x &&
    box.y < region.y + region.h && box.y + box.h > region.y;
  const boxes = all.filter(inside);
  const xsSet = new Set<number>(), ysSet = new Set<number>();
  for (const box of boxes) {
    xsSet.add(box.x - margin); xsSet.add(box.x + box.w + margin);
    ysSet.add(box.y - margin); ysSet.add(box.y + box.h + margin);
  }
  for (const box of [a, b]) { xsSet.add(box.x + box.w / 2); ysSet.add(box.y + box.h / 2); }
  const xs = [...xsSet].sort((p, q) => p - q), ys = [...ysSet].sort((p, q) => p - q);
  const nx = xs.length, ny = ys.length, nodes = nx * ny;
  if (scratch.size < nodes) {
    scratch.size = nodes;
    scratch.dist = new Float64Array(nodes * 2); scratch.previous = new Int32Array(nodes * 2);
    scratch.blockedH = new Uint8Array(nodes); scratch.blockedV = new Uint8Array(nodes);
  }
  const { dist, previous, blockedH, blockedV } = scratch;
  dist.fill(Number.POSITIVE_INFINITY, 0, nodes * 2);
  blockedH.fill(0, 0, nodes); blockedV.fill(0, 0, nodes);
  const firstAbove = (values: number[], value: number): number => {
    let low = 0, high = values.length;
    while (low < high) { const mid = (low + high) >> 1; if (values[mid] <= value) low = mid + 1; else high = mid; }
    return low;
  };
  // blockedH[j * nx + i]: the segment from (xs[i], ys[j]) to (xs[i + 1], ys[j]) enters an obstacle; blockedV likewise downwards.
  for (const box of boxes) {
    if (!box.opaque) continue;
    const left = box.x, right = box.x + box.w, top = box.y, bottom = box.y + box.h;
    for (let j = firstAbove(ys, top); j < ny && ys[j] < bottom; j++)
      for (let i = Math.max(0, firstAbove(xs, left) - 1); i < nx - 1 && xs[i] < right; i++) blockedH[j * nx + i] = 1;
    for (let i = firstAbove(xs, left); i < nx && xs[i] < right; i++)
      for (let j = Math.max(0, firstAbove(ys, top) - 1); j < ny - 1 && ys[j] < bottom; j++) blockedV[j * nx + i] = 1;
  }
  const ports = (box: LayoutBox): { node: number; edge: Point }[] => {
    const cx = box.x + box.w / 2, cy = box.y + box.h / 2;
    return ([[cx, box.y - margin, cx, box.y], [cx, box.y + box.h + margin, cx, box.y + box.h],
      [box.x - margin, cy, box.x, cy], [box.x + box.w + margin, cy, box.x + box.w, cy]] as const)
      .map(([px, py, ex, ey]) => ({ i: xs.indexOf(px), j: ys.indexOf(py), edge: [ex, ey] as Point }))
      .filter(port => port.i >= 0 && port.j >= 0).map(port => ({ node: port.j * nx + port.i, edge: port.edge }));
  };
  const BEND = 24;
  const heap: number[] = [], heapCost: number[] = [];
  const push = (state: number, cost: number): void => {
    let k = heap.length;
    heap.push(state); heapCost.push(cost);
    while (k > 0) {
      const parent = (k - 1) >> 1;
      if (heapCost[parent] <= cost) break;
      heap[k] = heap[parent]; heapCost[k] = heapCost[parent];
      k = parent;
    }
    heap[k] = state; heapCost[k] = cost;
  };
  const pop = (): number => {
    const top = heap[0];
    const lastState = heap.pop()!, lastCost = heapCost.pop()!;
    const size = heap.length;
    if (size) {
      let k = 0;
      for (;;) {
        const l = k * 2 + 1, r = l + 1;
        let m = -1, mCost = lastCost;
        if (l < size && heapCost[l] < mCost) { m = l; mCost = heapCost[l]; }
        if (r < size && heapCost[r] < mCost) m = r;
        if (m < 0) break;
        heap[k] = heap[m]; heapCost[k] = heapCost[m];
        k = m;
      }
      heap[k] = lastState; heapCost[k] = lastCost;
    }
    return top;
  };
  const starts = ports(a), goals = new Map(ports(b).map(port => [port.node, port.edge]));
  for (const start of starts) for (const axis of [0, 1]) {
    const state = start.node * 2 + axis;
    if (margin < dist[state]) { dist[state] = margin; previous[state] = -1; push(state, margin); }
  }
  const relax = (from: number, cost: number, next: number, axis: number, length: number, turn: boolean): void => {
    const state = next * 2 + axis, total = cost + length + (turn ? BEND : 0);
    if (total < dist[state]) { dist[state] = total; previous[state] = from; push(state, total); }
  };
  let reached = -1;
  while (heap.length) {
    const cost = heapCost[0], state = pop();
    if (cost > dist[state]) continue;
    const node = state >> 1, axis = state & 1;
    if (goals.has(node)) { reached = state; break; }
    const i = node % nx, j = (node - i) / nx;
    if (i < nx - 1 && !blockedH[node]) relax(state, cost, node + 1, 0, xs[i + 1] - xs[i], axis !== 0);
    if (i > 0 && !blockedH[node - 1]) relax(state, cost, node - 1, 0, xs[i] - xs[i - 1], axis !== 0);
    if (j < ny - 1 && !blockedV[node]) relax(state, cost, node + nx, 1, ys[j + 1] - ys[j], axis !== 1);
    if (j > 0 && !blockedV[node - nx]) relax(state, cost, node - nx, 1, ys[j] - ys[j - 1], axis !== 1);
  }
  if (reached < 0) return null;
  const path: Point[] = [];
  let first = -1;
  for (let state = reached; state >= 0; state = previous[state]) {
    first = state >> 1;
    path.unshift([xs[first % nx], ys[Math.floor(first / nx)]]);
  }
  const start = starts.find(port => port.node === first)!;
  return simplify([start.edge, ...path, goals.get(reached >> 1)!]);
}

/** Puts the numbered key on the route where it covers no box text and no other key. */
function placeKey(points: readonly Point[], boxes: readonly LayoutBox[], keys: readonly Rect[]): Point {
  let best: { point: Point; cost: number } | null = null;
  for (let k = 1; k < points.length; k++) {
    const [p, q] = [points[k - 1], points[k]];
    if (Math.abs(p[0] - q[0]) + Math.abs(p[1] - q[1]) < 24) continue;
    for (const f of [0.5, 0.3, 0.7, 0.15, 0.85]) {
      const point: Point = [p[0] + (q[0] - p[0]) * f, p[1] + (q[1] - p[1]) * f];
      const key: Rect = { x: point[0] - 10, y: point[1] - 10, w: 20, h: 20 };
      let cost = Math.abs(f - 0.5);
      for (const box of boxes) {
        const inter = key.x + key.w > box.x && key.x < box.x + box.w && key.y + key.h > box.y && key.y < box.y + box.h;
        if (!inter) continue;
        if (box.opaque) cost += 20;
        else if (key.y < box.y + LAYOUT.header) cost += 6;
      }
      for (const other of keys)
        if (key.x + key.w > other.x && key.x < other.x + other.w && key.y + key.h > other.y && key.y < other.y + other.h) cost += 15;
      if (!best || cost < best.cost) best = { point, cost };
    }
  }
  if (best) return best.point;
  const p = points[0], q = points[points.length - 1];
  return [(p[0] + q[0]) / 2, (p[1] + q[1]) / 2];
}

/** Automated geometry check: every pair of a drawn link and an instance box it crosses. */
export function linkCrossings(layout: Pick<StructureLayout, 'boxes' | 'links'>): { link: number; box: string }[] {
  const crossings: { link: number; box: string }[] = [];
  const obstacles = layout.boxes.filter(box => box.opaque);
  for (const link of layout.links)
    for (let k = 1; k < link.points.length; k++)
      for (const box of obstacles)
        if (segmentHits([link.points[k - 1], link.points[k]], box)) crossings.push({ link: link.number, box: box.id });
  return crossings;
}

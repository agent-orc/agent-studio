import type { LiveLink, LiveNode, LiveRect, LiveScene } from './live-structure.model';

/**
 * Automatic placement of the live structure scene in world units. The prototype
 * placed one installation by hand; here every frame sizes itself from what it
 * holds, so any reported installation gets a picture. `wide` puts machines side
 * by side, `tall` (phone, narrow panes) stacks them.
 */
export type LiveLayoutMode = 'wide' | 'tall';
export interface LiveCrossing { x: number; y: number; frame: LiveNode; horizontal: boolean; }
export interface LiveRoute { link: LiveLink; points: [number, number][]; length: number; crossings: LiveCrossing[]; }
export interface LiveLayout { mode: LiveLayoutMode; bounds: LiveRect; routes: LiveRoute[]; }

export const LIVE_METRICS = {
  unitW: 248, unitH: 164, gap: 14, pad: 20, machineHead: 62, frameHead: 50, chipH: 36, chipGap: 6,
  diskH: 104, emptyW: 248, emptyH: 92, externalW: 200, externalH: 84, boundaryPad: 36, boundaryTop: 56, laneGap: 64,
} as const;
const M = LIVE_METRICS;
const isFrame = (node: LiveNode): boolean => node.kind === 'machine' || node.kind === 'vm' || node.kind === 'group';
const chips = (node: LiveNode): LiveNode[] => isFrame(node) ? [] : node.children;
const columns = (count: number, mode: LiveLayoutMode): number => count <= 1 ? 1 : mode === 'tall' || count <= 4 ? 2 : 3;

function split(node: LiveNode): { frames: LiveNode[]; units: LiveNode[]; disks: LiveNode[] } {
  const frames: LiveNode[] = [], units: LiveNode[] = [], disks: LiveNode[] = [];
  for (const child of node.children) (child.kind === 'disk' && !child.source?.facts?.device ? disks : isFrame(child) && child.children.length ? frames : units).push(child);
  return { frames, units, disks };
}

/** Smallest width a node needs, bottom up. */
function natural(node: LiveNode, mode: LiveLayoutMode): number {
  if (!isFrame(node)) return M.unitW;
  if (!node.children.length) return M.emptyW;
  const { frames, units } = split(node);
  const cols = columns(units.length, mode);
  const grid = units.length ? cols * M.unitW + (cols - 1) * M.gap : 0;
  return Math.max(grid, ...frames.map(frame => natural(frame, mode)), M.emptyW - 2 * M.pad) + 2 * M.pad;
}

/** A service inside a container takes a row of its own, wide enough for its name and a fact. */
function unitHeight(node: LiveNode): number {
  const count = chips(node).length;
  return M.unitH + (count ? count * (M.chipH + M.chipGap) + M.chipGap : 0);
}

/** Places a node at a given width, top down, and returns its height. */
function place(node: LiveNode, x: number, y: number, width: number, mode: LiveLayoutMode): number {
  node.rect = { x, y, w: width, h: 0 };
  // A storage strip has a row for the disk bars and, where data locations are known, a row for their coverage.
  if (node.kind === 'disk') { node.rect.h = M.diskH + Math.max(0, node.disks.length - 1) * 20 + (node.locations.length ? 26 : 0); return node.rect.h; }
  if (!isFrame(node)) {
    node.rect.h = unitHeight(node);
    const list = chips(node);
    list.forEach((chip, index) => {
      chip.rect = { x: x + 10, y: y + node.rect.h - M.chipGap - (list.length - index) * (M.chipH + M.chipGap) + M.chipGap, w: width - 20, h: M.chipH };
    });
    return node.rect.h;
  }
  node.headH = node.kind === 'machine' ? M.machineHead : M.frameHead;
  if (!node.children.length) { node.rect.h = M.emptyH; return node.rect.h; }
  const { frames, units, disks } = split(node);
  const inner = width - 2 * M.pad;
  // The gutter under the header is as wide as the gutters between the rows: routes run in it.
  let cursor = y + node.headH + M.gap;
  for (const frame of frames) cursor += place(frame, x + M.pad, cursor, inner, mode) + M.gap * 1.5;
  if (units.length) {
    const cols = columns(units.length, mode), cell = (inner - (cols - 1) * M.gap) / cols;
    // Host services sit under the frames, with a caption line between them (see the renderer).
    if (frames.length) cursor += M.gap;
    for (let index = 0; index < units.length; index += cols) {
      const row = units.slice(index, index + cols);
      const height = Math.max(...row.map(unitHeight));
      row.forEach((unit, column) => { place(unit, x + M.pad + column * (cell + M.gap), cursor, cell, mode); unit.rect.h = Math.max(unit.rect.h, height); alignChips(unit); });
      cursor += height + M.gap;
    }
  }
  for (const disk of disks) cursor += place(disk, x + M.pad, cursor + M.gap * 0.5, inner, mode) + M.gap * 1.5;
  node.rect.h = cursor - y + M.pad - M.gap;
  return node.rect.h;
}

/** A unit stretched to its row keeps its service chips on the bottom edge. */
function alignChips(unit: LiveNode): void {
  const list = chips(unit);
  if (!list.length) return;
  const bottom = Math.max(...list.map(chip => chip.rect.y + chip.rect.h));
  const shift = unit.rect.y + unit.rect.h - M.chipGap - bottom;
  if (shift > 0) for (const chip of list) chip.rect.y += shift;
}

const union = (rects: LiveRect[]): LiveRect => {
  const x = Math.min(...rects.map(r => r.x)), y = Math.min(...rects.map(r => r.y));
  return { x, y, w: Math.max(...rects.map(r => r.x + r.w)) - x, h: Math.max(...rects.map(r => r.y + r.h)) - y };
};

/**
 * One arrangement: machines that hold something in rows of `perRow`, machines that hold
 * nothing stacked beside them, externals in a column on the left or in rows on top.
 */
function arrange(scene: LiveScene, mode: LiveLayoutMode, perRow: number, externals: 'left' | 'top'): LiveRect {
  const top = scene.boundary.children, outside = scene.outside;
  const big = top.filter(node => isFrame(node) && node.children.length), small = top.filter(node => !big.includes(node));
  const widths = new Map(big.map(node => [node, natural(node, mode)]));
  const outsideRows = Math.ceil(outside.length / Math.max(1, mode === 'tall' ? 2 : 5));
  const originX = 20 + (externals === 'left' && outside.length ? M.externalW + M.laneGap : 0) + M.boundaryPad;
  const originY = 20 + (externals === 'top' && outside.length ? outsideRows * (M.externalH + 20) + 28 : 0) + M.boundaryTop;
  let y = originY, right = originX;
  for (let index = 0; index < big.length; index += perRow) {
    let x = originX, height = 0;
    for (const node of big.slice(index, index + perRow)) {
      height = Math.max(height, place(node, x, y, widths.get(node)!, mode));
      x += widths.get(node)! + M.laneGap;
    }
    right = Math.max(right, x - M.laneGap);
    y += height + M.laneGap;
  }
  if (mode === 'wide' && big.length) {
    // Machines that only exist as a name: a column beside the rest.
    small.forEach((node, index) => place(node, right + M.laneGap, originY + index * (M.emptyH + 24), M.emptyW, mode));
  } else {
    const per = Math.max(1, Math.floor((Math.max(right - originX, M.emptyW) + 24) / (M.emptyW + 24)));
    small.forEach((node, index) => place(node, originX + (index % per) * (M.emptyW + 24), y + Math.floor(index / per) * (M.emptyH + 24), M.emptyW, mode));
  }
  const content = top.length ? union(top.map(node => node.rect)) : { x: originX, y: originY, w: M.emptyW, h: M.emptyH };
  const boundary = scene.boundary.rect = { x: content.x - M.boundaryPad, y: content.y - M.boundaryTop, w: content.w + 2 * M.boundaryPad, h: content.h + M.boundaryTop + M.boundaryPad };
  if (externals === 'left') {
    const step = M.externalH + 36;
    const start = boundary.y + Math.max(36, (boundary.h - outside.length * step + 36) / 2);
    outside.forEach((node, index) => { node.rect = { x: 20, y: start + index * step, w: M.externalW, h: M.externalH }; });
  } else {
    const per = Math.ceil(outside.length / Math.max(1, outsideRows)), w = Math.min(M.externalW + 40, (boundary.w - (per - 1) * 20) / Math.max(1, per));
    outside.forEach((node, index) => { node.rect = { x: boundary.x + (index % per) * (w + 20), y: 20 + Math.floor(index / per) * (M.externalH + 20), w, h: M.externalH }; });
  }
  const all = union([boundary, ...outside.map(node => node.rect)]);
  return { x: all.x - 24, y: all.y - 24, w: all.w + 48, h: all.h + 48 };
}

/**
 * Lays the scene out for a pane of the given aspect (width over height): of the
 * arrangements that are possible it takes the one that shows the installation largest.
 */
export function layoutLiveScene(scene: LiveScene, mode: LiveLayoutMode, aspect = 1.6): LiveLayout {
  const frames = scene.boundary.children.filter(node => isFrame(node) && node.children.length).length;
  const options: { perRow: number; externals: 'left' | 'top' }[] = [];
  for (let perRow = 1; perRow <= Math.max(1, mode === 'tall' ? 1 : frames); perRow++)
    for (const externals of mode === 'tall' ? ['top'] as const : ['left', 'top'] as const) options.push({ perRow, externals });
  let best = options[0], scale = -1;
  for (const option of options) {
    const size = arrange(scene, mode, option.perRow, option.externals);
    const fit = Math.min(aspect / size.w, 1 / size.h);
    if (fit > scale * 1.02) { scale = fit; best = option; }
  }
  const bounds = arrange(scene, mode, best.perRow, best.externals);
  return { mode, bounds, routes: routeLinks(scene, best.externals) };
}

const ancestorsOf = (node: LiveNode): LiveNode[] => { const out: LiveNode[] = []; for (let n = node.parent; n; n = n.parent) out.push(n); return out; };
const holds = (node: LiveNode): boolean => isFrame(node) && node.children.length > 0;
/** A unit that sits in the grid of a frame, with a free gutter above and below its row. */
const inGrid = (node: LiveNode): boolean => !!node.parent && holds(node.parent) && !holds(node) && node.kind !== 'disk';

/**
 * Routes that keep to the free space of the plan. A unit is left through the gutter
 * above or below its row, a frame, storage strip or external sideways; the long run
 * between the two ends uses a lane no node stands in: the gap between two machines,
 * the padding strip of the machine both ends share, or the strip beside stacked
 * machines. Routes with a common end share the last stretch, like a trunk.
 */
function routeLinks(scene: LiveScene, externals: 'left' | 'top'): LiveRoute[] {
  // A service inside a container routes from its container.
  const anchor = (node: LiveNode): LiveNode => node.parent && !isFrame(node.parent) && node.parent.kind !== 'boundary' ? node.parent : node;
  const topOf = (node: LiveNode): LiveNode => { let current = node; while (current.parent && current.parent.kind !== 'boundary') current = current.parent; return current; };
  const count = new Map<LiveNode, number>(), used = new Map<LiveNode, number>();
  for (const link of scene.links) for (const node of [anchor(link.a), anchor(link.b)]) count.set(node, (count.get(node) ?? 0) + 1);
  const slot = (node: LiveNode): number => { const index = (used.get(node) ?? 0) + 1; used.set(node, index); return index / ((count.get(node) ?? 1) + 1); };

  const lane = (a: LiveNode, b: LiveNode): number => {
    if (a.parent === b.parent && inGrid(a) && inGrid(b)) return b.rect.x - M.gap / 2;
    const ta = topOf(a), tb = topOf(b);
    if (ta === tb) return ta.rect.x + M.pad / 2;
    const left = ta.rect.x <= tb.rect.x ? ta : tb, right = left === ta ? tb : ta;
    if (left.rect.x + left.rect.w <= right.rect.x) return (left.rect.x + left.rect.w + right.rect.x) / 2;
    return Math.min(ta.rect.x, tb.rect.x) - M.boundaryPad / 2;
  };
  const escape = (node: LiveNode, other: LiveNode, laneX: number): { point: [number, number]; y: number } => {
    const r = node.rect, t = slot(node);
    if (inGrid(node)) {
      const up = other.rect.y + other.rect.h / 2 < r.y;
      return { point: [r.x + r.w * t, up ? r.y : r.y + r.h], y: up ? r.y - M.gap / 2 : r.y + r.h + M.gap / 2 };
    }
    if (!node.parent && externals === 'top') return { point: [r.x + r.w * t, r.y + r.h], y: r.y + r.h + 12 };
    const y = holds(node) ? r.y + node.headH * (0.3 + 0.4 * t) : r.y + r.h * t;
    return { point: [laneX < r.x + r.w / 2 ? r.x : r.x + r.w, y], y };
  };
  return scene.links.map(link => {
    const a = anchor(link.a), b = anchor(link.b);
    // A tunnel and a backup route keep a little apart from the plain routes of the same lane.
    const x = lane(a, b) + (link.style === 'tunnel' ? 10 : link.style === 'backup' ? -10 : 0);
    const from = escape(a, b, x), to = escape(b, a, x);
    const raw: [number, number][] = [from.point, [from.point[0], from.y], [x, from.y], [x, to.y], [to.point[0], to.y], to.point];
    const direct = a.parent === b.parent && inGrid(a) && inGrid(b) && from.y === to.y;
    const points = tidy(direct ? [from.point, [from.point[0], from.y], [to.point[0], to.y], to.point] : raw);
    let length = 0;
    for (let i = 1; i < points.length; i++) length += Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]);
    return { link, points, length, crossings: crossings(link, points) };
  });
}

/** Drops repeated points and points in the middle of a straight run. */
function tidy(points: [number, number][]): [number, number][] {
  const out: [number, number][] = [];
  for (const point of points) {
    const last = out.at(-1), before = out.at(-2);
    if (last && last[0] === point[0] && last[1] === point[1]) continue;
    if (last && before && ((before[0] === last[0] && last[0] === point[0]) || (before[1] === last[1] && last[1] === point[1]))) out.pop();
    out.push(point);
  }
  return out;
}

/** Where a route crosses the frames it leaves or enters (boundary, machine, VM, group). */
function crossings(link: LiveLink, points: [number, number][]): LiveCrossing[] {
  const A = ancestorsOf(link.a), B = ancestorsOf(link.b);
  const frames = [...A.filter(node => !B.includes(node)), ...B.filter(node => !A.includes(node))];
  const out: LiveCrossing[] = [];
  for (const frame of frames) {
    const r = frame.rect;
    for (let i = 1; i < points.length; i++) {
      const p = points[i - 1], q = points[i];
      if (p[1] === q[1]) {
        if (p[1] <= r.y || p[1] >= r.y + r.h) continue;
        for (const x of [r.x, r.x + r.w]) if (x > Math.min(p[0], q[0]) && x < Math.max(p[0], q[0])) out.push({ x, y: p[1], frame, horizontal: true });
      } else {
        if (p[0] <= r.x || p[0] >= r.x + r.w) continue;
        for (const y of [r.y, r.y + r.h]) if (y > Math.min(p[1], q[1]) && y < Math.max(p[1], q[1])) out.push({ x: p[0], y, frame, horizontal: false });
      }
    }
  }
  return out;
}

import type { LiveCamera, LiveView } from './live-structure.camera';
import { containerScale, leastContainerScale } from './live-structure.facts';
import type { LiveNode, LiveScene } from './live-structure.model';

/**
 * Where the camera of the live structure view goes (AGT-4024, owner feedback 3 and 4):
 * the view a jump lands on, the way back, and the place the view keeps when the
 * data or the arrangement changes under it. Pure functions over the camera and
 * the scene, so the rules can be tested without a canvas.
 */

const holds = (node: LiveNode): boolean => (node.kind === 'machine' || node.kind === 'vm' || node.kind === 'group') && node.children.length > 0;
const inUnit = (node: LiveNode): boolean => !!node.parent && (node.parent.kind === 'container' || node.parent.kind === 'service');
const free = (cam: LiveCamera): { w: number; h: number } => ({
  w: Math.max(40, cam.width - cam.safe.left - cam.safe.right), h: Math.max(40, cam.height - cam.safe.top - cam.safe.bottom) });

/** The box a jump frames: a service inside a container is shown with its container. */
export const jumpSubject = (node: LiveNode): LiveNode => inUnit(node) ? node.parent! : node;

/**
 * The view one action brings a node into: a machine or virtual machine whole, anything
 * else at the container level (state, image, version, restarts, uptime and current job
 * on the node) with its surroundings still in view. A node that does not fit the pane at
 * that level (a storage strip as wide as its machine, on a narrow pane) keeps the level
 * and shows its start, where its name and first facts are.
 */
export function jumpView(cam: LiveCamera, node: LiveNode): LiveView {
  const subject = jumpSubject(node), r = subject.rect;
  if (holds(subject)) return cam.viewFor(r, 0, 0.92);
  const area = free(cam), fit = Math.min(area.w / r.w, area.h / r.h) * 0.94;
  const k = Math.max(cam.kMin, Math.min(cam.kMax, containerScale(subject), Math.max(fit, leastContainerScale(subject))));
  const along = (start: number, size: number, room: number): number => size * k <= room ? start + size / 2 : start + (room / 2 - 12) / k;
  return { x: along(r.x, r.w, area.w), y: along(r.y, r.h, area.h), k };
}

/** The closest view of a node: it fills the pane, up to a width that still reads as one card. */
export function closestView(cam: LiveCamera, node: LiveNode): LiveView {
  const subject = jumpSubject(node), phone = cam.width <= 720;
  return holds(subject) ? cam.viewFor(subject.rect, 0, 0.92) : cam.viewFor(subject.rect, phone ? cam.width - 24 : 720, phone ? 0.94 : 0.86);
}

/** True when two views show the same thing: within a few screen pixels and two percent of scale. */
export function sameView(a: LiveView, b: LiveView): boolean {
  return Math.abs(Math.log(a.k / b.k)) < 0.02 && Math.hypot(a.x - b.x, a.y - b.y) * a.k < 6;
}

/** The node the view is zoomed into: the innermost one that fills most of the free area. */
export function focusOf(cam: LiveCamera, scene: LiveScene): LiveNode {
  const area = free(cam);
  let current = scene.boundary;
  for (let next: LiveNode | undefined = current; next;) {
    current = next;
    next = current.children.find(child => {
      const r = child.rect, share = child.children.length ? 0.78 : 0.56;
      return cam.x >= r.x && cam.x <= r.x + r.w && cam.y >= r.y && cam.y <= r.y + r.h && (r.w * cam.k >= area.w * share || r.h * cam.k >= area.h * share);
    });
  }
  return current;
}

/** Where the centre of the view lies, as an offset from each node around it, innermost first. */
export type LiveAnchor = readonly { id: string; dx: number; dy: number }[];

/**
 * Taken before a new arrangement: the nodes under the centre of the view. `restoreAnchor`
 * then puts the centre back on the innermost of them that still exists, so a data refresh
 * or a node that appears elsewhere does not move what the owner is looking at.
 */
export function anchorOf(cam: LiveCamera, scene: LiveScene): LiveAnchor {
  const out: { id: string; dx: number; dy: number }[] = [];
  const inside = (node: LiveNode): boolean => cam.x >= node.rect.x && cam.x <= node.rect.x + node.rect.w && cam.y >= node.rect.y && cam.y <= node.rect.y + node.rect.h;
  const walk = (node: LiveNode): void => {
    out.unshift({ id: node.id, dx: cam.x - node.rect.x, dy: cam.y - node.rect.y });
    const child = node.children.find(inside);
    if (child) walk(child);
  };
  walk(scene.outside.find(inside) ?? scene.boundary);
  return out;
}
export function restoreAnchor(cam: LiveCamera, scene: LiveScene, anchor: LiveAnchor): void {
  for (const item of anchor) {
    const node = scene.byId.get(item.id);
    if (!node) continue;
    cam.x = node.rect.x + item.dx; cam.y = node.rect.y + item.dy;
    return;
  }
}

/** A view the owner came from, with what was selected there. */
export interface LiveStop { view: LiveView; selected: string | null; }
/** The way back: the views before each jump, newest last. */
export class LiveHistory {
  private readonly stops: LiveStop[] = [];
  get length(): number { return this.stops.length; }
  push(stop: LiveStop): void {
    const last = this.stops.at(-1);
    if (last && sameView(last.view, stop.view) && last.selected === stop.selected) return;
    this.stops.push(stop);
    if (this.stops.length > 30) this.stops.shift();
  }
  pop(): LiveStop | null { return this.stops.pop() ?? null; }
}

/** Nodes that match what was typed into the jump field, best first. Every word must occur in the name, role, kind or reported name. */
export function searchNodes(scene: LiveScene, query: string, kindLabel: Readonly<Record<string, string>>, limit = 8): LiveNode[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return [];
  const scored: { node: LiveNode; score: number }[] = [];
  for (const node of scene.order) {
    if (node.kind === 'boundary') continue;
    const name = node.name.toLowerCase(), rest = `${node.role} ${kindLabel[node.kind] ?? ''} ${node.technical ?? ''} ${node.source?.label ?? ''}`.toLowerCase();
    if (!words.every(word => name.includes(word) || rest.includes(word))) continue;
    const first = words[0];
    scored.push({ node, score: (name.startsWith(first) ? 0 : name.split(' ').some(part => part.startsWith(first)) ? 1 : name.includes(first) ? 2 : 3)
      // Among equal matches a container comes before the services inside it, and those before frames and storage.
      + (inUnit(node) ? 0.25 : node.kind === 'container' ? 0 : node.kind === 'service' ? 0.1 : 0.5) });
  }
  return scored.sort((a, b) => a.score - b.score || a.node.name.localeCompare(b.node.name, 'en')).slice(0, limit).map(item => item.node);
}

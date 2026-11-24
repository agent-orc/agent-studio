import type { LiveCamera } from './live-structure.camera';
import type { LiveLayout } from './live-structure.layout';
import type { LiveRect, LiveScene } from './live-structure.model';
import { clamp, type LivePalette } from './live-structure.painter';

export interface LiveMinimapTransform { k: number; ox: number; oy: number; }

/**
 * Key plan: the whole installation in a small canvas, with the open warnings as
 * marks and the part the main canvas shows as a frame. Returns the transform, so
 * a click on the plan can be turned into a place in the world.
 */
export function drawLiveMinimap(target: HTMLCanvasElement, scene: LiveScene, layout: LiveLayout, pal: LivePalette, cam: LiveCamera,
  warnings: boolean): LiveMinimapTransform | null {
  const g = target.getContext('2d');
  if (!g) return null;
  const d = Math.min(window.devicePixelRatio || 1, 3), cw = target.clientWidth, ch = target.clientHeight;
  if (target.width !== Math.round(cw * d) || target.height !== Math.round(ch * d)) { target.width = Math.round(cw * d); target.height = Math.round(ch * d); }
  g.setTransform(d, 0, 0, d, 0, 0); g.clearRect(0, 0, cw, ch);
  const b = layout.bounds, k = Math.min((cw - 6) / b.w, (ch - 6) / b.h);
  const ox = (cw - b.w * k) / 2 - b.x * k, oy = (ch - b.h * k) / 2 - b.y * k;
  const box = (r: LiveRect): [number, number, number, number] => [Math.round(r.x * k + ox) + 0.5, Math.round(r.y * k + oy) + 0.5, Math.max(1, Math.round(r.w * k)), Math.max(1, Math.round(r.h * k))];
  g.lineWidth = 1;
  g.setLineDash([3, 2]); g.strokeStyle = pal.ink3; g.strokeRect(...box(scene.boundary.rect)); g.setLineDash([]);
  for (const node of scene.order) {
    if (node.kind !== 'machine' && node.kind !== 'vm' && node.kind !== 'group' && node.kind !== 'external' && node.kind !== 'target') continue;
    const q = box(node.rect);
    g.fillStyle = node.kind === 'machine' ? pal.s1 : pal.s3; g.fillRect(...q); g.strokeStyle = pal.ink2; g.strokeRect(...q);
  }
  if (warnings) for (const warning of scene.warnings) {
    const r = warning.node.rect;
    g.fillStyle = warning.tone === 'danger' ? pal.danger : pal.warn; g.fillRect(Math.round((r.x + r.w / 2) * k + ox) - 2, Math.round((r.y + r.h / 2) * k + oy) - 2, 4, 4);
  }
  const s = cam.safe;
  const x0 = clamp(cam.wx(s.left) * k + ox, 1, cw - 1), y0 = clamp(cam.wy(s.top) * k + oy, 1, ch - 1);
  const x1 = clamp(cam.wx(cam.width - s.right) * k + ox, 1, cw - 1), y1 = clamp(cam.wy(cam.height - s.bottom) * k + oy, 1, ch - 1);
  g.fillStyle = pal.ink; g.globalAlpha = 0.08; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1;
  g.strokeStyle = pal.ink; g.lineWidth = 1.5; g.strokeRect(Math.round(x0) + 0.5, Math.round(y0) + 0.5, Math.round(x1 - x0), Math.round(y1 - y0));
  return { k, ox, oy };
}

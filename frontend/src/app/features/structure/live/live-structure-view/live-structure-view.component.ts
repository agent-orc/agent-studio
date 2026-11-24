import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import type { StructureData } from '../../structure.model';
import { LiveStructureDetailsComponent } from '../live-structure-details/live-structure-details.component';
import { LiveStructureLayersComponent } from '../live-structure-layers/live-structure-layers.component';
import { LiveStructureOutlineComponent } from '../live-structure-outline/live-structure-outline.component';
import { LiveCamera } from '../live-structure.camera';
import { LIVE_LEVELS, type LiveLevel } from '../live-structure.facts';
import { attachLiveGestures } from '../live-structure.gestures';
import { layoutLiveScene, type LiveLayout, type LiveLayoutMode } from '../live-structure.layout';
import { drawLiveMinimap, type LiveMinimapTransform } from '../live-structure.minimap';
import { LIVE_LAYERS, buildLiveScene, diskUsed, pathOf, percent, type LiveLayer, type LiveNode, type LiveScene } from '../live-structure.model';
import { readLivePalette, type LivePalette } from '../live-structure.painter';
import { LiveRenderer } from '../live-structure.renderer';
import { LiveStructureSourcesService } from '../live-structure.sources';

type Layers = Record<LiveLayer, boolean>;
/** Dossier decision D4, option A: performance, network and warnings are on at first; storage and backup are one switch away. */
const DEFAULT_LAYERS: Layers = { perf: true, network: true, storage: false, backup: false, warnings: true };

/**
 * Live structure view (AGT-4024): the installation as one zoomable plan with
 * five layers, an outline and card details. This component owns the canvas, the
 * camera and the selection; the drawing itself is in `LiveRenderer`.
 */
@Component({
  selector: 'app-live-structure-view',
  standalone: true,
  imports: [DisclosureMarkerComponent, LiveStructureDetailsComponent, LiveStructureLayersComponent,
    LiveStructureOutlineComponent, SegmentedControlComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-structure-view.component.html',
  styleUrl: './live-structure-view.component.scss',
  host: { '(keydown)': 'onKeydown($event)' },
})
export class LiveStructureViewComponent {
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  private readonly sources = inject(LiveStructureSourcesService);
  readonly data = input.required<StructureData>();
  /** True while structure changes arrive over the hub. */
  readonly live = input(false);
  /** Comma separated layer ids and a node id from the address, applied once. */
  readonly initialLayers = input<string | null>(null);
  readonly initialFocus = input<string | null>(null);
  /** Called with the layers and the focus whenever they change, so the host can keep them in the address. */
  readonly persist = input<((state: { layers: string; focus: string | null }) => void) | null>(null);

  readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  readonly stageRef = viewChild.required<ElementRef<HTMLElement>>('stage');
  readonly minimapRef = viewChild<ElementRef<HTMLCanvasElement>>('minimap');

  readonly layers = signal<Layers>({ ...DEFAULT_LAYERS });
  /** Active layers, the one switched on last first. */
  readonly order = signal<LiveLayer[]>(['warnings', 'network', 'perf']);
  readonly selected = signal<string | null>(null);
  readonly selectedLink = signal<string | null>(null);
  readonly phone = signal(false);
  readonly mode = signal<LiveLayoutMode>('wide');
  readonly panelTab = signal<'details' | 'outline'>('details');
  readonly sheet = signal<'peek' | 'open'>('peek');
  readonly crumbs = signal<readonly { id: string; name: string }[]>([]);
  readonly level = signal<LiveLevel>(1);
  readonly selectedLevel = signal<number | null>(null);
  /** Screen box and level of every node once the camera rests: the hook the specs click and read. */
  readonly probes = signal<readonly { id: string; level: number; box: string }[]>([]);
  readonly panelTabs = [{ value: 'details', label: 'Details' }, { value: 'outline', label: 'Outline' }] as const;

  readonly scene = computed<LiveScene>(() => buildLiveScene(this.data(), this.sources.sources()));
  readonly selectedNode = computed(() => { const id = this.selected(); return id ? this.scene().byId.get(id) ?? null : null; });
  readonly selectedLinkModel = computed(() => { const id = this.selectedLink(); return id ? this.scene().links.find(link => link.id === id) ?? null : null; });
  readonly levelName = computed(() => LIVE_LEVELS[this.level() - 1]);
  readonly activeLayers = computed(() => LIVE_LAYERS.filter(layer => this.layers()[layer.id]).map(layer => layer.id).join(','));
  readonly sheetTitle = computed(() => this.selectedLinkModel()?.name ?? this.selectedNode()?.name ?? this.scene().boundary.name);
  /** One fact per layer for the switch, so a layer says something even while it is off. */
  readonly summaries = computed<Record<LiveLayer, string>>(() => {
    const scene = this.scene(), totals = scene.totals;
    const links = scene.links.filter(link => link.layer === 'network'), down = links.filter(link => link.health.tone === 'danger').length;
    const disks = scene.order.flatMap(node => node.disks);
    return {
      perf: totals.totalSlots ? `${totals.busySlots} of ${totals.totalSlots} slots busy` : `${totals.reporting} hosts reporting`,
      network: links.length ? `${links.length} ${links.length === 1 ? 'route' : 'routes'}${down ? `, ${down} down` : ''}` : 'no route reported',
      storage: disks.length ? `fullest disk ${percent(Math.max(...disks.map(diskUsed)))}` : 'not observed',
      backup: !scene.backup.observed ? 'not observed' : scene.backup.locations ? `${scene.backup.covered} of ${scene.backup.locations} covered` : scene.backup.health.word,
      warnings: `${scene.warnings.length} open`,
    };
  });

  private camera = new LiveCamera();
  private renderer: LiveRenderer | null = null;
  private raf = 0;
  private lastFrame = 0;
  private shape = '';
  private aspect = 0;
  private hover: string | null = null;
  private hoverLink: string | null = null;
  private minimapTransform: LiveMinimapTransform | null = null;
  private layout: LiveLayout | null = null;
  private palette: LivePalette | null = null;

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.sources.refresh();
    const timer = setInterval(() => { if (document.visibilityState === 'visible') this.sources.refresh(); }, 30_000);
    destroyRef.onDestroy(() => { clearInterval(timer); cancelAnimationFrame(this.raf); });
    effect(() => {
      const layers = this.initialLayers(), focus = this.initialFocus();
      untracked(() => {
        if (layers !== null) {
          const on = layers.split(',');
          const next = Object.fromEntries(LIVE_LAYERS.map(layer => [layer.id, on.includes(layer.id)])) as Layers;
          this.layers.set(next);
          this.order.set(LIVE_LAYERS.map(layer => layer.id).filter(id => next[id]).reverse());
        }
        if (focus) this.selected.set(focus);
      });
    });
    effect(() => { const scene = this.scene(), mode = this.mode(); untracked(() => this.applyScene(scene, mode)); });
    effect(() => { this.layers(); this.order(); this.selected(); this.selectedLink(); untracked(() => this.invalidate()); });
    afterNextRender(() => {
      const canvas = this.canvasRef().nativeElement, stage = this.stageRef().nativeElement;
      // Without a 2D context (a browser without canvas, a test DOM) the outline and the card details still work.
      if (!canvas.getContext?.('2d') || typeof ResizeObserver === 'undefined' || typeof matchMedia === 'undefined') return;
      this.renderer = new LiveRenderer(canvas, this.camera);
      const motion = matchMedia('(prefers-reduced-motion: reduce)');
      const palette = (): void => {
        this.camera.reduced = motion.matches;
        this.palette = readLivePalette(this.element.nativeElement);
        this.renderer!.setPalette(this.palette);
        this.invalidate();
      };
      palette();
      motion.addEventListener('change', palette);
      // The theme is an attribute of the document; the canvas reads its colours again when it changes.
      const theme = new MutationObserver(palette);
      theme.observe(document.documentElement, { attributes: true, attributeFilter: ['data-studio-theme', 'class', 'style'] });
      const resize = new ResizeObserver(() => this.measure());
      resize.observe(stage);
      resize.observe(this.element.nativeElement);
      const detach = attachLiveGestures(canvas, this.camera, {
        tap: (x, y, double) => this.onTap(x, y, double),
        hover: (x, y) => this.onHover(x, y),
        leave: () => { if (this.hover || this.hoverLink) { this.hover = null; this.hoverLink = null; canvas.dataset['pointing'] = 'false'; this.invalidate(); } },
        changed: () => this.invalidate(),
      });
      void document.fonts?.ready.then(palette);
      destroyRef.onDestroy(() => { detach(); resize.disconnect(); theme.disconnect(); motion.removeEventListener('change', palette); });
      this.measure();
    });
  }

  private measure(): void {
    const stage = this.stageRef().nativeElement, hostWidth = this.element.nativeElement.clientWidth;
    const width = stage.clientWidth, height = stage.clientHeight;
    const phone = hostWidth > 0 && hostWidth < 720;
    this.phone.set(phone);
    if (!width || !height || !this.renderer) return;
    this.mode.set(phone || width / height < 0.82 ? 'tall' : 'wide');
    this.camera.measure(width, height, { left: 0, top: 44, right: 0, bottom: phone ? 44 : 12 }, true);
    this.renderer.resize(width, height);
    this.applyScene(this.scene(), this.mode());
  }

  /** New data or a new arrangement. The camera stays where it is unless the set of nodes changed. */
  private applyScene(scene: LiveScene, mode: LiveLayoutMode): void {
    if (!this.renderer || !this.camera.width) return;
    const safe = this.camera.safe;
    const aspect = Math.max(0.3, (this.camera.width - safe.left - safe.right) / Math.max(120, this.camera.height - safe.top - safe.bottom));
    // The arrangement follows the pane; it is kept while the pane changes little, so a resize does not reshuffle the plan.
    if (!this.aspect || Math.abs(aspect - this.aspect) / this.aspect > 0.25) this.aspect = aspect;
    const layout = this.layout = layoutLiveScene(scene, mode, this.aspect);
    this.renderer.setScene(scene, layout);
    this.camera.setBounds(layout.bounds);
    const shape = `${mode}\n${this.aspect}\n${scene.order.map(node => node.id).join('\n')}`;
    if (shape !== this.shape) {
      this.shape = shape;
      const focus = this.selectedNode();
      if (focus && focus.kind !== 'boundary') this.camera.fitRect(focus.rect, !focus.children.length || focus.kind === 'container' || focus.kind === 'service', true);
      else this.home(true);
    }
    this.invalidate();
  }

  private invalidate(): void {
    if (this.raf || !this.renderer) return;
    // "busy" until the camera rests again and the read-outs are written.
    this.canvasRef().nativeElement.dataset['busy'] = 'true';
    this.raf = requestAnimationFrame(time => this.frame(time));
  }

  private frame(time: number): void {
    this.raf = 0;
    const renderer = this.renderer!;
    const dt = Math.min(0.05, (time - this.lastFrame) / 1000 || 0.016);
    this.lastFrame = time;
    const moving = this.camera.step(time, dt);
    renderer.draw({ layers: this.layers(), order: this.order(), selected: this.selected(), selectedLink: this.selectedLink(), hover: this.hover, hoverLink: this.hoverLink });
    const minimap = this.minimapRef()?.nativeElement;
    if (minimap?.clientWidth && this.layout && this.palette)
      this.minimapTransform = drawLiveMinimap(minimap, this.scene(), this.layout, this.palette, this.camera, this.layers().warnings);
    this.readouts();
    if (moving) this.invalidate(); else this.settle();
  }

  /** The node the view is zoomed into: the innermost one that fills most of the free area. */
  private focusNode(): LiveNode {
    const cam = this.camera, safe = cam.safe;
    const sw = cam.width - safe.left - safe.right, sh = cam.height - safe.top - safe.bottom;
    let current = this.scene().boundary;
    for (let next: LiveNode | undefined = current; next;) {
      current = next;
      next = current.children.find(child => {
        const r = child.rect, share = child.children.length ? 0.78 : 0.56;
        return cam.x >= r.x && cam.x <= r.x + r.w && cam.y >= r.y && cam.y <= r.y + r.h && (r.w * cam.k >= sw * share || r.h * cam.k >= sh * share);
      });
    }
    return current;
  }

  private readouts(): void {
    const focus = this.focusNode(), renderer = this.renderer!;
    const path = pathOf(focus).map(node => ({ id: node.id, name: node.kind === 'boundary' ? 'Installation' : node.name }));
    if (path.map(item => item.id).join('\n') !== this.crumbs().map(item => item.id).join('\n')) this.crumbs.set(path);
    const selected = this.selectedNode();
    const subject = selected && selected.kind !== 'boundary' ? selected : focus;
    // With the whole installation in view the level is the deepest one any machine shows.
    const level = subject.kind === 'boundary'
      ? Math.max(1, ...subject.children.map(child => renderer.levels.get(child.id) ?? 1)) as LiveLevel
      : renderer.levels.get(subject.id) ?? 1;
    if (level !== this.level()) this.level.set(level);
    const own = selected ? renderer.levels.get(selected.id) ?? null : null;
    if (own !== this.selectedLevel()) this.selectedLevel.set(own);
  }

  private settle(): void {
    const renderer = this.renderer!;
    const probes = this.scene().order.filter(node => node.kind !== 'boundary').map(node => {
      const r = renderer.rect(node.rect);
      return { id: node.id, level: renderer.levels.get(node.id) ?? 0, box: [r.x, r.y, r.w, r.h].map(Math.round).join(',') };
    });
    if (JSON.stringify(probes) !== JSON.stringify(this.probes())) this.probes.set(probes);
    const canvas = this.canvasRef().nativeElement;
    canvas.dataset['frames'] = String(renderer.frames);
    canvas.dataset['busy'] = 'false';
  }

  private state(): Parameters<LiveRenderer['pick']>[2] {
    return { layers: this.layers(), order: this.order(), selected: this.selected(), selectedLink: this.selectedLink(), hover: this.hover, hoverLink: this.hoverLink };
  }
  private onHover(x: number, y: number): void {
    const hit = this.renderer?.pick(x, y, this.state()) ?? null;
    const node = hit?.node ?? null, link = hit?.link ?? null;
    if (node === this.hover && link === this.hoverLink) return;
    this.hover = node; this.hoverLink = link;
    this.canvasRef().nativeElement.dataset['pointing'] = String(!!hit);
    this.invalidate();
  }
  private onTap(x: number, y: number, double: boolean): void {
    const hit = this.renderer?.pick(x, y, this.state()) ?? null;
    if (double) { if (hit?.node) this.zoomTo(hit.node); else { this.camera.zoomAt(x, y, 1.9); this.invalidate(); } return; }
    if (hit?.link) this.chooseLink(hit.link);
    else if (hit?.node) this.choose(hit.node);
    else this.clear();
  }

  choose(id: string, reveal = true): void {
    this.selected.set(id);
    this.selectedLink.set(null);
    this.panelTab.set('details');
    if (this.phone()) this.sheet.set('open');
    if (reveal) this.reveal(id);
    this.emitRemember();
  }
  chooseLink(id: string): void {
    this.selectedLink.set(id);
    this.selected.set(null);
    this.panelTab.set('details');
    if (this.phone()) this.sheet.set('open');
    this.emitRemember();
  }
  clear(): void {
    this.selected.set(null);
    this.selectedLink.set(null);
    if (this.phone()) this.sheet.set('peek');
    this.emitRemember();
  }
  /** Selects a node and frames it. Leaves frame to show their content; frames frame themselves. */
  zoomTo(id: string): void {
    const node = this.scene().byId.get(id);
    if (!node) return;
    if (node.kind === 'boundary') { this.clear(); this.camera.fitAll(); this.invalidate(); return; }
    this.choose(id, false);
    if (this.phone()) this.sheet.set('peek');
    this.camera.fitRect(node.rect, node.kind !== 'machine' && node.kind !== 'vm' && node.kind !== 'group');
    this.invalidate();
  }
  fit(): void { this.camera.fitAll(); this.invalidate(); }
  /**
   * The view a pane opens with. A wide pane shows the whole installation. A phone would show it
   * too small to read, so it opens on the top of the plan at reading width; Fit still shows all.
   */
  private home(instant: boolean): void {
    const cam = this.camera, all = cam.viewFor(cam.bounds, 0, 0.97);
    const k = Math.min(0.62, ((cam.width - cam.safe.left - cam.safe.right) / cam.bounds.w) * 0.98);
    if (this.mode() !== 'tall' || k <= all.k * 1.15) { cam.fitAll(instant); return; }
    cam.go({ x: cam.bounds.x + cam.bounds.w / 2, y: cam.bounds.y + (cam.height - cam.safe.top - cam.safe.bottom) / 2 / k, k }, instant);
  }
  zoomBy(factor: number): void { this.camera.zoomAt(this.camera.ox, this.camera.oy, factor); this.invalidate(); }

  /** Pans just enough to bring a selected node into the free area. */
  private reveal(id: string): void {
    const node = this.scene().byId.get(id), renderer = this.renderer;
    if (!node || !renderer) return;
    const cam = this.camera, r = renderer.rect(node.rect), margin = 14;
    const x0 = cam.safe.left + margin, x1 = cam.width - cam.safe.right - margin, y0 = cam.safe.top + margin, y1 = cam.height - cam.safe.bottom - margin;
    if (r.w > x1 - x0 || r.h > y1 - y0) {
      const cx = r.x + r.w / 2, cy = r.y + r.h / 2;
      if (cx < x0 || cx > x1 || cy < y0 || cy > y1) this.camera.fitRect(node.rect, !node.children.length);
    } else {
      const dx = r.x < x0 ? x0 - r.x : r.x + r.w > x1 ? x1 - (r.x + r.w) : 0;
      const dy = r.y < y0 ? y0 - r.y : r.y + r.h > y1 ? y1 - (r.y + r.h) : 0;
      if (dx || dy) cam.go({ x: cam.x - dx / cam.k, y: cam.y - dy / cam.k, k: cam.k });
    }
    this.invalidate();
  }

  toggleLayer(layer: LiveLayer): void {
    const on = !this.layers()[layer];
    this.layers.update(layers => ({ ...layers, [layer]: on }));
    this.order.update(order => on ? [layer, ...order.filter(item => item !== layer)] : order.filter(item => item !== layer));
    const link = this.selectedLinkModel();
    if (link && !on && link.layer === layer) this.selectedLink.set(null);
    this.emitRemember();
  }
  toggleSheet(): void { this.sheet.update(sheet => sheet === 'open' ? 'peek' : 'open'); }
  private emitRemember(): void { this.persist()?.({ layers: this.activeLayers(), focus: this.selected() }); }

  onMinimap(event: PointerEvent): void {
    const transform = this.minimapTransform, target = this.minimapRef()?.nativeElement;
    if (!transform || !target || (event.type === 'pointermove' && !event.buttons)) return;
    const box = target.getBoundingClientRect();
    this.camera.centreOn((event.clientX - box.left - transform.ox) / transform.k, (event.clientY - box.top - transform.oy) / transform.k);
    this.invalidate();
  }

  onKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (event.metaKey || event.ctrlKey || event.altKey || !target || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
    const onCanvas = target === this.canvasRef().nativeElement, cam = this.camera, step = 90 / cam.k;
    const layer = LIVE_LAYERS[Number(event.key) - 1];
    if (layer) this.toggleLayer(layer.id);
    else if (event.key === '+' || event.key === '=') this.zoomBy(1.5);
    else if (event.key === '-' || event.key === '_') this.zoomBy(1 / 1.5);
    else if (event.key === '0') this.fit();
    else if (event.key === 'Escape') {
      const focus = this.focusNode();
      if (this.selected() || this.selectedLink()) this.clear();
      else if (focus.parent && focus.parent.kind !== 'boundary') this.camera.fitRect(focus.parent.rect, false);
      else this.camera.fitAll();
      this.invalidate();
    } else if (!onCanvas) return;
    else if (event.key === 'ArrowLeft') cam.panBy(-step, 0);
    else if (event.key === 'ArrowRight') cam.panBy(step, 0);
    else if (event.key === 'ArrowUp') cam.panBy(0, -step);
    else if (event.key === 'ArrowDown') cam.panBy(0, step);
    else if (event.key === 'Enter' && this.selected()) this.zoomTo(this.selected()!);
    else return;
    this.invalidate();
    event.preventDefault();
    event.stopPropagation();
  }
}

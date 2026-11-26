import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { ModalStackService } from '../../../../services/modal-stack.service';
import type { StructureData } from '../../structure.model';
import { LiveStructureDetailsComponent } from '../live-structure-details/live-structure-details.component';
import { LiveStructureJumpComponent } from '../live-structure-jump/live-structure-jump.component';
import { LiveStructureLayersComponent } from '../live-structure-layers/live-structure-layers.component';
import { LiveStructureOutlineComponent } from '../live-structure-outline/live-structure-outline.component';
import { LiveCamera } from '../live-structure.camera';
import { LIVE_LEVELS, type LiveLevel } from '../live-structure.facts';
import { attachLiveGestures } from '../live-structure.gestures';
import { layoutLiveScene, type LiveLayout, type LiveLayoutMode } from '../live-structure.layout';
import { drawLiveMinimap, type LiveMinimapTransform } from '../live-structure.minimap';
import { LIVE_LAYERS, buildLiveScene, diskUsed, pathOf, percent, type LiveLayer, type LiveNode, type LiveScene } from '../live-structure.model';
import { LiveHistory, anchorOf, closestView, focusOf, jumpView, restoreAnchor, sameView } from '../live-structure.navigation';
import { readLivePalette, type LivePalette } from '../live-structure.painter';
import { LiveRenderer } from '../live-structure.renderer';
import { LiveStructureSourcesService } from '../live-structure.sources';

type Layers = Record<LiveLayer, boolean>;
/** Dossier decision D4, option A: performance, network and warnings are on at first; storage and backup are one switch away. */
const DEFAULT_LAYERS: Layers = { perf: true, network: true, storage: false, backup: false, warnings: true };

/**
 * Live structure view (AGT-4024): the installation as one zoomable plan with
 * five layers, an outline, a jump field and card details. This component owns
 * the canvas, the camera and the selection; the drawing itself is in
 * `LiveRenderer`, the rules of where the camera goes in `live-structure.navigation`.
 */
@Component({
  selector: 'app-live-structure-view',
  standalone: true,
  imports: [DisclosureMarkerComponent, LiveStructureDetailsComponent, LiveStructureJumpComponent, LiveStructureLayersComponent,
    LiveStructureOutlineComponent, SegmentedControlComponent, StatusIndicatorComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-structure-view.component.html',
  styleUrl: './live-structure-view.component.scss',
  host: { '(keydown)': 'onKeydown($event)' },
})
export class LiveStructureViewComponent {
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  private readonly sources = inject(LiveStructureSourcesService);
  private readonly modalStack = inject(ModalStackService);
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
  readonly jumpRef = viewChild(LiveStructureJumpComponent);

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
  /** The breadcrumb as it is shown: a long path keeps its start and its end, a phone keeps the end. */
  readonly crumbTrail = computed(() => {
    const path = this.crumbs(), keep = this.phone() ? 1 : 2;
    const hidden = (index: number): boolean => path.length > keep + 1 && index < path.length - keep && (this.phone() || index > 0);
    return path.map((crumb, index) => ({ ...crumb, hidden: hidden(index), gap: hidden(index) && !hidden(index + 1) }));
  });
  readonly level = signal<LiveLevel>(1);
  readonly selectedLevel = signal<number | null>(null);
  /** Screen box, level and number of facts drawn of every node once the camera rests: the hook the specs click and read. */
  readonly probes = signal<readonly { id: string; level: number; box: string; facts: number }[]>([]);
  /** Views to go back to: one for each jump, fit or breadcrumb step. */
  readonly backSteps = signal(0);
  /** The view shows something else than the whole installation. */
  readonly away = signal(false);
  /** Back has somewhere to go: an earlier view, a selection to drop, or a closer view to leave. */
  readonly canBack = computed(() => this.backSteps() > 0 || !!this.selected() || !!this.selectedLink() || this.away());
  readonly panelTabs = [{ value: 'details', label: 'Details' }, { value: 'outline', label: 'Outline' }] as const;

  /** The same read again (same revision and time, same optional reads) keeps its scene, so nothing is laid out or drawn for it. */
  private built: { key: string; sources: unknown; scene: LiveScene } | null = null;
  readonly scene = computed<LiveScene>(() => {
    const data = this.data(), sources = this.sources.sources();
    const key = `${data.revision ?? ''}|${data.generatedAt ?? ''}|${data.nodes.length}|${data.links.length}`;
    if (!this.built || this.built.key !== key || this.built.sources !== sources || data.revision == null)
      this.built = { key, sources, scene: buildLiveScene(data, sources) };
    return this.built.scene;
  });
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
  private aspect = 0;
  /** The scene the canvas shows now; its arrangement is what a new one is anchored to. */
  private drawn: LiveScene | null = null;
  private readonly history = new LiveHistory();
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
    // The Studio hands Escape to whatever registered last (ModalStackService); a plain key handler never sees it.
    this.modalStack.pushUntilDestroyed('live-structure-back', () => this.onEscape(), destroyRef);
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
    // The way back and the jump field lie over the top of the plan, the level read-out over its foot.
    this.camera.measure(width, height, { left: 0, top: 44, right: 0, bottom: 48 }, true);
    this.renderer.resize(width, height);
    this.applyScene(this.scene(), this.mode());
  }

  /**
   * New data or a new arrangement. Only the first one frames something; after that the view
   * keeps its zoom and stays on the node it rests on, whatever appeared or moved elsewhere.
   */
  private applyScene(scene: LiveScene, mode: LiveLayoutMode): void {
    if (!this.renderer || !this.camera.width) return;
    const safe = this.camera.safe;
    const aspect = Math.max(0.3, (this.camera.width - safe.left - safe.right) / Math.max(120, this.camera.height - safe.top - safe.bottom));
    // The arrangement follows the pane; it is kept while the pane changes little, so a resize does not reshuffle the plan.
    if (!this.aspect || Math.abs(aspect - this.aspect) / this.aspect > 0.25) this.aspect = aspect;
    const anchor = this.drawn ? anchorOf(this.camera, this.drawn) : null;
    const layout = this.layout = layoutLiveScene(scene, mode, this.aspect);
    this.renderer.setScene(scene, layout);
    this.camera.setBounds(layout.bounds);
    if (anchor) restoreAnchor(this.camera, scene, anchor);
    else {
      const focus = this.selectedNode();
      if (focus && focus.kind !== 'boundary') this.camera.go(jumpView(this.camera, focus), true);
      else this.home(true);
    }
    this.drawn = scene;
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

  private readouts(): void {
    const focus = focusOf(this.camera, this.scene()), renderer = this.renderer!;
    const selected = this.selectedNode();
    // At the container level on the selected node the breadcrumb leads down to it.
    const chosen = selected && (renderer.levels.get(selected.id) ?? 0) >= 3 && pathOf(selected).includes(focus) ? selected : focus;
    const path = pathOf(chosen).map(node => ({ id: node.id, name: node.kind === 'boundary' ? 'Installation' : node.name }));
    if (path.map(item => item.id).join('\n') !== this.crumbs().map(item => item.id).join('\n')) this.crumbs.set(path);
    // The read-out names the deepest level a node in view shows: what the owner can read right now.
    const cam = this.camera, inView = (node: LiveNode): boolean => { const x = cam.sx(node.rect.x + node.rect.w / 2), y = cam.sy(node.rect.y + node.rect.h / 2);
      return x >= 0 && x <= cam.width && y >= 0 && y <= cam.height; };
    const level = Math.max(1, ...this.scene().order.filter(inView).map(node => renderer.levels.get(node.id) ?? 1)) as LiveLevel;
    if (level !== this.level()) this.level.set(level);
    const own = selected ? renderer.levels.get(selected.id) ?? null : null;
    if (own !== this.selectedLevel()) this.selectedLevel.set(own);
    const away = !sameView(cam.view, cam.viewFor(cam.bounds, 0, 0.97));
    if (away !== this.away()) this.away.set(away);
  }

  private settle(): void {
    const renderer = this.renderer!;
    const probes = this.scene().order.filter(node => node.kind !== 'boundary').map(node => {
      const r = renderer.rect(node.rect);
      return { id: node.id, level: renderer.levels.get(node.id) ?? 0, box: [r.x, r.y, r.w, r.h].map(Math.round).join(','), facts: renderer.shown.get(node.id) ?? 0 };
    });
    if (JSON.stringify(probes) !== JSON.stringify(this.probes())) this.probes.set(probes);
    const canvas = this.canvasRef().nativeElement;
    canvas.dataset['frames'] = String(renderer.frames);
    canvas.dataset['view'] = `${this.camera.x.toFixed(1)},${this.camera.y.toFixed(1)},${this.camera.k.toFixed(3)}`;
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
    if (double) { if (hit?.node) this.jumpTo(hit.node); else { this.camera.zoomAt(x, y, 1.9); this.invalidate(); } return; }
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
  /** Notes the view the owner is leaving, so Back and Escape return to it. */
  private leave(to: Parameters<LiveCamera['go']>[0]): void {
    const from = this.camera.view;
    if (sameView(from, to)) return;
    this.history.push({ view: from, selected: this.selected() });
    this.backSteps.set(this.history.length);
  }
  /**
   * The one action that brings a node into view (double click, Enter, an outline row, the jump
   * field, a breadcrumb): it is selected and shown at the container level, a machine whole. The
   * same action on a node already shown that way goes to the closest level, and back again.
   */
  jumpTo(id: string): void {
    const node = this.scene().byId.get(id), cam = this.camera;
    if (!node) return;
    if (node.kind === 'boundary') { this.fit(); this.clear(); return; }
    const near = jumpView(cam, node);
    const to = this.selected() === id && sameView(cam.view, near) ? closestView(cam, node) : near;
    this.leave(to);
    this.choose(id, false);
    if (this.phone()) this.sheet.set('peek');
    cam.go(to);
    this.invalidate();
  }
  fit(): void { const cam = this.camera; this.leave(cam.viewFor(cam.bounds, 0, 0.97)); cam.fitAll(); this.invalidate(); }
  /** The way back: the view before the last jump, then the selection is dropped, then one frame out, then everything. */
  back(): void {
    const stop = this.history.pop(), cam = this.camera;
    this.backSteps.set(this.history.length);
    if (stop) {
      this.selected.set(stop.selected && this.scene().byId.has(stop.selected) ? stop.selected : null);
      this.selectedLink.set(null);
      this.emitRemember();
      cam.go(stop.view);
    } else if (this.selected() || this.selectedLink()) this.clear();
    else {
      const parent = focusOf(cam, this.scene()).parent;
      if (parent && parent.kind !== 'boundary') cam.go(jumpView(cam, parent)); else cam.fitAll();
    }
    this.invalidate();
  }
  /** Escape is Back while the focus is in this view (or nowhere), except in a field, which keeps its own Escape. */
  private onEscape(): boolean {
    const active = document.activeElement as HTMLElement | null;
    if (active && active !== document.body && !this.element.nativeElement.contains(active)) return false;
    if ((active && /^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName)) || !this.canBack()) return false;
    this.back();
    return true;
  }
  focusCanvas(): void { this.canvasRef().nativeElement.focus({ preventScroll: true }); }
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
      if (cx < x0 || cx > x1 || cy < y0 || cy > y1) cam.go(jumpView(cam, node));
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
    // Arrow keys move the plan while the focus is on it or on one of its controls; elsewhere they scroll lists as usual.
    const onStage = this.stageRef().nativeElement.contains(target), cam = this.camera, step = event.shiftKey ? 270 : 90;
    const layer = LIVE_LAYERS[Number(event.key) - 1];
    if (layer) this.toggleLayer(layer.id);
    else if (event.key === '+' || event.key === '=') this.zoomBy(1.5);
    else if (event.key === '-' || event.key === '_') this.zoomBy(1 / 1.5);
    else if (event.key === '0') this.fit();
    else if (event.key === '/') this.jumpRef()?.focus();
    else if (!onStage) return;
    else if (event.key === 'ArrowLeft') cam.panBy(-step, 0);
    else if (event.key === 'ArrowRight') cam.panBy(step, 0);
    else if (event.key === 'ArrowUp') cam.panBy(0, -step);
    else if (event.key === 'ArrowDown') cam.panBy(0, step);
    else if (event.key === 'Enter' && target === this.canvasRef().nativeElement && this.selected()) this.jumpTo(this.selected()!);
    else return;
    this.invalidate();
    event.preventDefault();
    event.stopPropagation();
  }
}

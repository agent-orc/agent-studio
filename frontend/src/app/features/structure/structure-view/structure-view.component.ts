import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, OnDestroy, Type, afterNextRender, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DatePipe, NgComponentOutlet } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthSessionState } from '../../../services/auth.service';
import { JobsHubClient, type StructureSubscription } from '../../../services/jobs-hub-client.service';
import { studioProjectSlug } from '../../../services/studio-project-slug.util';
import { replaceStudioRouteQuery } from '../../studio-shell/services/studio-route';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';
import { SegmentedControlComponent } from '@studio/ui/components/segmented-control/segmented-control.component';
import { LAYOUT, layoutStructure, leafDetail, type LayoutBox, type RoutedLink } from '../structure-layout';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { applyStructureDelta, factLines, isGroup, isPlacement, isStructuralDelta, isUndeclared, kindText, observationGlyph, observationText,
  observationWord, projectStructure, summaryText, visibleBoxes,
  type StructureBox, type StructureData, type StructureDelta, type StructureObservation } from '../structure.model';

interface LibraryChannel { name: string; version: string; publishedAt: string | null; }
interface LibraryRegistry {
  projectId: string; artifact: string; provider: string; package: string; repository: string | null;
  private: boolean; state: 'current' | 'unavailable' | 'stale'; checkedAt: string | null;
  lastPublishAt: string | null; channels: LibraryChannel[];
}
interface LibraryConsumer {
  projectId: string; project: string; package: string; version: string; source: string;
  latestStable: string | null; releasesBehind: number | null; ownershipMapped: boolean;
}
interface StructureLibrary { registries: LibraryRegistry[]; consumers: LibraryConsumer[]; }
const STATUS: Record<string, { tone: 'ok' | 'warn' | 'down' | 'unknown'; word: string }> = {
  up: { tone: 'ok', word: 'Healthy' }, degraded: { tone: 'warn', word: 'Degraded' },
  down: { tone: 'down', word: 'Down' }, invalid: { tone: 'down', word: 'Check invalid' },
  unknown: { tone: 'unknown', word: 'Not observed yet' },
};
let instances = 0;

@Component({
  selector: 'app-structure-view',
  standalone: true,
  imports: [DatePipe, DisclosureMarkerComponent, NgComponentOutlet, SegmentedControlComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './structure-view.component.html',
  styleUrl: './structure-view.component.scss',
})
export class StructureViewComponent implements OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  private readonly hub = inject(JobsHubClient);
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  readonly projectName = input<string | null>(null);
  readonly projectSlug = studioProjectSlug;
  readonly embedded = input(false);
  readonly hostOnly = input(false);
  /** The Operations tab offers the live canvas (AGT-4024); embedded uses keep the classic drawing. */
  readonly liveCanvas = input(false);
  readonly mode = signal<'live' | 'classic'>('classic');
  readonly modeOptions = [{ value: 'live', label: 'Live' }, { value: 'classic', label: 'Classic' }] as const;
  /** Layers and focus of the live canvas as the address carried them when the view opened. */
  readonly liveLayers = signal<string | null>(null);
  readonly liveFocus = signal<string | null>(null);
  private liveState: { layers: string; focus: string | null } | null = null;
  /** The canvas is its own chunk, loaded when the live drawing is first shown. */
  readonly liveComponent = signal<Type<unknown> | null>(null);
  readonly liveInputs = computed(() => ({ data: this.data(), live: this.live(), initialLayers: this.liveLayers(), initialFocus: this.liveFocus(),
    persist: (state: { layers: string; focus: string | null }) => this.rememberLive(state) }));
  readonly data = signal<StructureData>({ nodes: [], links: [] });
  readonly viewOptions = [{ value: 'host', label: 'Host' }, { value: 'container', label: 'Container' }] as const;
  readonly levelOptions = [{ value: 1, label: '1' }, { value: 2, label: '2' }, { value: 3, label: '3' }] as const;
  readonly overlayOptions = [
    { value: 'health', label: 'Health' }, { value: 'version', label: 'Version' },
    { value: 'drift', label: 'Drift' }, { value: 'load', label: 'Load' },
  ] as const;
  readonly error = signal<string | null>(null);
  readonly loaded = signal(false);
  readonly library = signal<StructureLibrary>({ registries: [], consumers: [] });
  readonly libraryError = signal<string | null>(null);
  readonly refreshing = signal(false);
  readonly view = signal<'host' | 'container'>('host');
  readonly level = signal<1 | 2 | 3>(2);
  readonly overlay = signal<'health' | 'version' | 'drift' | 'load'>('health');
  readonly toggles = signal<Record<string, boolean>>({});
  readonly selected = signal<string | null>(null);
  readonly environment = signal('');
  readonly environments = computed(() => this.data().nodes.filter(node => node.kind === 'environment'));
  readonly selectedNode = computed(() => this.data().nodes.find(node => node.id === this.selected()) ?? null);
  readonly selectedLinks = computed(() => this.data().links.filter(link => link.from === this.selected() || link.to === this.selected()));
  readonly selectedFacts = computed(() => { const node = this.selectedNode(); return node ? factLines(node) : []; });
  /** True while deltas of this scope arrive over the hub; the view then reads only to converge. */
  readonly live = signal(false);
  readonly undeclared = isUndeclared;
  /** Placement of every node, and the machines a declaration names. */
  private readonly placementOf = computed(() => new Map(this.data().nodes.map(node => [node.id, node.parentId])));
  private readonly declaredMachines = computed(() => new Set(this.data().nodes
    .filter(node => node.provenance === 'declared' && isPlacement(node)).map(node => node.id)));
  /**
   * Hatching marks the exception: something undeclared inside a declared machine.
   * Where nothing is declared the chip alone says so and the picture stays calm.
   */
  hatched(box: StructureBox): boolean {
    const parent = this.placementOf().get(box.id);
    return box.observation?.difference === 'undeclared' && !!parent && this.declaredMachines().has(parent);
  }
  /** Content width of the drawing area; the layout wraps to it. */
  readonly canvasWidth = signal(880);
  readonly roots = computed(() => projectStructure(this.data(), this.view(), this.level(), this.toggles()));
  readonly visible = computed(() => visibleBoxes(this.roots()));
  readonly layout = computed(() => {
    const start = performance.now();
    const layout = layoutStructure(this.roots(), this.data(), this.view(), this.canvasWidth());
    return { ...layout, ms: Math.round((performance.now() - start) * 10) / 10 };
  });
  readonly headerHeight = LAYOUT.header;
  readonly arrowId = `structure-arrow-${++instances}`;
  readonly summaryText = summaryText;
  readonly kindText = kindText;
  readonly stateWord = observationWord;
  readonly stateGlyph = observationGlyph;
  private readGeneration = 0;
  private refreshTimer?: ReturnType<typeof setInterval>;
  private lastReadAt = 0;
  private loadedKey: string | null = null;
  private subscription: StructureSubscription | null = null;
  private subscribedKey: string | null = null;
  private subscribeGeneration = 0;

  ownerDescription(box: StructureBox): string {
    return box.otherProject ? `other project · ${box.ownerLabel ?? box.project}` : kindText(box);
  }
  /** Second line of a box: instances say how they are observed or placed, groups their kind. */
  placementDetail(item: LayoutBox): string {
    return item.opaque && !isGroup(item.box) ? leafDetail(item.box, this.view()) : this.ownerDescription(item.box);
  }
  overlayDetail(item: LayoutBox): string {
    const observation = item.box.observation;
    if (this.overlay() === 'version') return observation?.version
      ? `${observation.behind ? '↓ behind · ' : ''}${observation.version}${observation.expectedVersion ? ` · expected ${observation.expectedVersion}` : ''}`
      : 'Version not observed';
    if (this.overlay() === 'drift') return observation?.difference === 'differs'
      ? `≠ declared${observation.version ? ` · ${observation.version}` : ''}${observation.expectedVersion ? ` / ${observation.expectedVersion}` : ''}`
      : (observation?.difference ?? 'declared-only').replaceAll('-', ' ');
    if (this.overlay() === 'load') return observation?.cpuPercent != null || observation?.memoryUsedBytes != null || observation?.activeGates != null
      ? `${observation.cpuPercent != null ? `${observation.cpuPercent.toFixed(0)}% CPU` : 'CPU not observed'} · ${observation.memoryUsedBytes != null ? `${(observation.memoryUsedBytes / 1024 ** 3).toFixed(1)} GiB memory` : 'Memory not observed'}${observation.activeGates != null ? ` · ${observation.activeGates} active gates` : ''}`
      : 'Load not observed';
    return observation?.detail ?? this.placementDetail(item);
  }
  /** Calls and artifact flows carry an arrow; tunnels and transports are symmetric. */
  arrowFor(kind: string): string | null {
    return kind === 'call' || kind === 'artifact' || kind === 'publish' ? `url(#${this.arrowId})` : null;
  }
  linkObservation(link: RoutedLink): StructureObservation | null {
    return this.data().links.find(item => item.kind === link.kind && item.label === link.label)?.observation ?? null;
  }
  /** Probed round trip of a link, where the host measures one. */
  linkLatency(link: RoutedLink): string {
    const latency = this.data().links.filter(item => item.kind === link.kind && item.label === link.label)
      .map(item => item.facts?.latencyMs).filter((value): value is number => value != null);
    return latency.length ? ` · ${Math.max(...latency).toFixed(1)} ms` : '';
  }
  status(box: StructureBox): { tone: string; word: string } | null {
    return box.status ? STATUS[box.status] ?? STATUS['unknown'] : null;
  }
  accessibleLabel(box: StructureBox): string {
    const observed = observationText(box);
    return `${box.label}, ${kindText(box)}` +
      (observed ? `, declared, ${observed}` : '') +
      (this.status(box) ? `, ${this.status(box)!.word}` : '') +
      `, ${observationWord(box.observation)}` +
      (isUndeclared(box) ? ', observed, not declared' : '') +
      (box.replicas > 1 ? `, ${box.replicas} replicas` : '') +
      (box.placements > 1 ? `, ${box.placements} placements` : '') +
      (box.otherProject ? `, other project, ${box.ownerLabel ?? box.project}` : '') +
      (box.collapsed && box.children.length ? `, ${summaryText(box)}` : '');
  }

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => this.ngOnDestroy());
    afterNextRender(() => {
      const main = this.element.nativeElement.querySelector<HTMLElement>('.structure-main');
      if (this.mode() === 'classic') main?.focus();
      if (!main) return;
      this.measureWidth(main);
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(() => this.measureWidth(main));
      observer.observe(main);
      destroyRef.onDestroy(() => observer.disconnect());
    });
    void this.loadLibrary(false);
    effect(() => {
      if (this.mode() === 'live' && !untracked(this.liveComponent))
        void import('../live/live-structure-view/live-structure-view.component').then(m => this.liveComponent.set(m.LiveStructureViewComponent));
    });
    effect(() => {
      this.projectName();
      this.auth.status();
      this.embedded();
      this.hostOnly();
      this.liveCanvas();
      untracked(() => {
        const query = new URLSearchParams(this.embedded() ? '' : location.hash.split('?')[1] ?? '');
        try {
          const saved = JSON.parse(localStorage.getItem(this.preferenceKey()) || '{}') as
            { view?: 'host' | 'container'; level?: 1 | 2 | 3; overlay?: 'health' | 'version' | 'drift' | 'load'; mode?: string; layers?: string };
          this.mode.set(this.startMode(query, saved.mode));
          this.liveLayers.set(query.get('layers') ?? (typeof saved.layers === 'string' ? saved.layers : null));
          if (saved.view === 'host' || saved.view === 'container') this.view.set(saved.view);
          if (saved.level === 1 || saved.level === 2 || saved.level === 3) this.level.set(saved.level);
          if (saved.overlay && ['health', 'version', 'drift', 'load'].includes(saved.overlay)) this.overlay.set(saved.overlay);
        } catch { this.mode.set(this.startMode(query, undefined)); }
        this.liveFocus.set(query.get('focus'));
        if (query.get('view') === 'host' || query.get('view') === 'container') this.view.set(query.get('view') as 'host' | 'container');
        if (['1', '2', '3'].includes(query.get('level') ?? '')) this.level.set(Number(query.get('level')) as 1 | 2 | 3);
        if (['health', 'version', 'drift', 'load'].includes(query.get('overlay') ?? '')) this.overlay.set(query.get('overlay') as 'health' | 'version' | 'drift' | 'load');
        // Hosts that only report exist in the whole picture alone, so the live canvas reads without an environment filter.
        this.environment.set(this.mode() === 'live' ? '' : query.get('env') ?? '');
        this.selected.set(query.get('focus'));
        if (this.hostOnly()) this.view.set('host');
        void this.load();
      });
    });
    // Live updates: join the hub group of this scope whenever the socket is up and the
    // scope or environment changed. Membership ends with a connection, so this runs again
    // after every reconnect.
    const removeDeltaListener = this.hub.onStructureDelta<StructureDelta>(delta => this.onDelta(delta));
    destroyRef.onDestroy(removeDeltaListener);
    effect(() => {
      const connected = this.hub.connected();
      const scope = this.scope();
      const environment = this.environment() || null;
      untracked(() => void this.subscribe(connected, scope, environment));
    });
    this.startRefresh();
  }

  ngOnDestroy(): void {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
    this.unsubscribe();
  }

  /**
   * Fallback read. With deltas arriving it only converges every 15 seconds and the
   * entity tag makes that a 304; without the hub it reads every 5 seconds.
   */
  private startRefresh(): void {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
    if (document.visibilityState !== 'visible') return;
    this.refreshTimer = setInterval(() => {
      if (!this.live() || Date.now() - this.lastReadAt >= 15_000) void this.load();
    }, 5_000);
  }

  private scope(): string { return this.projectName() ? `project:${this.projectName()}` : 'workspace'; }

  private async subscribe(connected: boolean, scope: string, environment: string | null): Promise<void> {
    const key = `${scope}\n${environment ?? ''}`;
    const generation = ++this.subscribeGeneration;
    if (this.subscribedKey !== null && this.subscribedKey !== key) this.unsubscribe();
    if (!connected) { this.subscription = null; this.live.set(false); return; }
    const subscription = await this.hub.subscribeStructure(scope, environment);
    if (generation !== this.subscribeGeneration) return;
    this.subscription = subscription;
    this.subscribedKey = subscription ? key : null;
    this.live.set(subscription !== null);
    // Anything that changed before the subscription existed is read once.
    if (subscription && this.loaded() && subscription.revision !== (this.data().revision ?? 0)) void this.load();
  }

  private unsubscribe(): void {
    if (this.subscribedKey !== null) {
      const [scope, environment] = this.subscribedKey.split('\n');
      this.hub.unsubscribeStructure(scope, environment || null);
    }
    this.subscribedKey = null;
    this.subscription = null;
    this.live.set(false);
  }

  private onDelta(delta: StructureDelta): void {
    const subscription = this.subscription;
    if (!subscription || delta.scope !== subscription.scope || (delta.environment ?? null) !== (subscription.environment ?? null)) return;
    const data = this.data();
    if (delta.revision <= (data.revision ?? 0)) return;
    // A gap, or nodes and links that come and go, is read as a whole so the order matches the server's.
    if (delta.baseRevision !== data.revision || isStructuralDelta(data, delta)) { void this.load(); return; }
    this.data.set(applyStructureDelta(data, delta));
  }

  @HostListener('document:visibilitychange') onVisibilityChange(): void {
    this.startRefresh();
    if (document.visibilityState === 'visible') void this.load();
  }

  async loadLibrary(refresh: boolean): Promise<void> {
    this.refreshing.set(refresh);
    try {
      this.library.set(await firstValueFrom(refresh
        ? this.http.post<StructureLibrary>('/api/v1/operations/structure/library/refresh', {})
        : this.http.get<StructureLibrary>('/api/v1/operations/structure/library')));
      this.libraryError.set(null);
    } catch { this.libraryError.set('Registry inventory is unavailable.'); }
    finally { this.refreshing.set(false); }
  }

  registryLabel(provider: string): string {
    return ({ npm: 'npm', nuget: 'NuGet', 'docker-hub': 'Docker Hub', github: 'GitHub',
      jfrog: 'JFrog', internal: 'Internal registry' } as Record<string, string>)[provider] ?? provider;
  }

  private measureWidth(main: HTMLElement): void {
    const style = getComputedStyle(main);
    const width = main.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0');
    if (width > 0) this.canvasWidth.set(Math.max(480, Math.floor(width)));
  }

  private async load(): Promise<void> {
    const generation = ++this.readGeneration;
    const key = `${this.scope()}\n${this.environment()}`;
    // The revision is the entity tag: an unchanged structure answers 304 and keeps the nodes.
    const revision = this.loadedKey === key ? this.data().revision : undefined;
    this.lastReadAt = Date.now();
    try {
      const data = await firstValueFrom(this.http.get<StructureData>('/api/v1/operations/structure',
        { params: { scope: this.scope(), ...(this.environment() ? { environment: this.environment() } : {}) },
          ...(revision ? { headers: { 'If-None-Match': `"${revision}"` } } : {}) }));
      if (generation !== this.readGeneration) return;
      this.data.set(data);
      this.loadedKey = key;
      this.error.set(null);
      this.loaded.set(true);
    } catch (error) {
      if (generation !== this.readGeneration) return;
      if (error instanceof HttpErrorResponse && error.status === 304) { this.error.set(null); return; }
      this.error.set('Structure observations could not be loaded.');
    }
  }

  /**
   * The live canvas opens first in the Operations tab of the workspace. An address
   * that names the classic drawing's view, level or overlay still opens the classic
   * drawing, so links from before the canvas keep their target.
   */
  private startMode(query: URLSearchParams, saved: string | undefined): 'live' | 'classic' {
    if (!this.liveCanvas() || this.embedded() || this.hostOnly()) return 'classic';
    const asked = query.get('mode');
    if (asked === 'live' || asked === 'classic') return asked;
    if (query.has('view') || query.has('level') || query.has('overlay')) return 'classic';
    if (saved === 'live' || saved === 'classic') return saved;
    return this.projectName() ? 'classic' : 'live';
  }
  setMode(mode: 'live' | 'classic'): void {
    if (mode === this.mode()) return;
    this.mode.set(mode);
    if (mode === 'live' && this.environment()) { this.environment.set(''); void this.load(); }
    this.remember();
  }
  rememberLive(state: { layers: string; focus: string | null }): void {
    this.liveState = state;
    this.remember();
  }
  setView(view: 'host' | 'container'): void {
    if (this.hostOnly()) return;
    this.view.set(view);
    this.remember();
  }
  setEnvironment(value: string): void { this.environment.set(value); this.remember(); void this.load(); }
  setLevel(level: 1 | 2 | 3): void {
    this.level.set(level);
    this.remember();
  }
  setOverlay(overlay: 'health' | 'version' | 'drift' | 'load'): void {
    this.overlay.set(overlay);
    this.remember();
  }
  lastObserved(observation?: StructureObservation | null): string {
    return observation?.observedAt ? `Last observed ${new Date(observation.observedAt).toLocaleString('en-US')}` : 'No report';
  }
  toggle(box: StructureBox): void {
    this.selected.set(box.id);
    this.remember();
    if (box.children.length) this.toggles.update(current => ({ ...current, [box.id]: !box.collapsed }));
  }
  focus(id: string): void {
    this.selected.set(id);
    this.remember();
    this.element.nativeElement.querySelector<HTMLElement>(`[data-node-id="${CSS.escape(id)}"]`)?.focus();
  }
  linkHost(id: string): string {
    const nodes = new Map(this.data().nodes.map(node => [node.id, node]));
    let current = nodes.get(id);
    while (current && !current.id.startsWith('machine:') &&
      !['physical', 'cloud-server', 'vm', 'host', 'server'].includes(current.kind))
      current = current.parentId ? nodes.get(current.parentId) : undefined;
    return (current?.id ?? id).replace(/^machine:/, '');
  }
  private preferenceKey(): string { return `studio.structure.${this.auth.status()?.user?.id ?? 'local'}.${this.hostOnly() ? 'settings' : this.projectName() ?? 'workspace'}`; }
  private remember(): void {
    const live = this.mode() === 'live', layers = this.liveState?.layers ?? this.liveLayers();
    if (!this.embedded()) replaceStudioRouteQuery(live
      ? { mode: 'live', layers, focus: this.liveState ? this.liveState.focus : this.liveFocus(), view: null, level: null, overlay: null, env: null }
      : { mode: this.liveCanvas() ? 'classic' : null, layers: null, view: this.view(), level: String(this.level()), overlay: this.overlay(), env: this.environment(), focus: this.selected() });
    try {
      localStorage.setItem(this.preferenceKey(), JSON.stringify({ view: this.view(), level: this.level(), overlay: this.overlay(),
        ...(this.liveCanvas() ? { mode: this.mode(), ...(layers !== null ? { layers } : {}) } : {}) }));
    } catch { /* Preference storage is optional. */ }
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    // The live canvas owns its keys (1 to 5 switch layers there).
    if (this.mode() === 'live' || event.altKey || event.ctrlKey || event.metaKey ||
        event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
    const key = event.key.toLowerCase();
    if (key === '1' || key === '2' || key === '3') this.setLevel(Number(key) as 1 | 2 | 3);
    else if (key === 'h') this.setView('host');
    else if (key === 'c') this.setView('container');
    else if (key === 'escape') { this.selected.set(null); this.remember(); }
    else return;
    event.preventDefault();
  }

  onCanvasKeydown(event: KeyboardEvent): void {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
    const scroller = this.element.nativeElement.querySelector<HTMLElement>('.structure-main');
    if (!scroller) return;
    const step = event.key === 'ArrowDown' ? 40 : event.key === 'ArrowUp' ? -40 :
      event.key === 'PageDown' ? scroller.clientHeight : event.key === 'PageUp' ? -scroller.clientHeight : null;
    if (step !== null) scroller.scrollTop += step;
    else if (event.key === 'Home') scroller.scrollTop = 0;
    else if (event.key === 'End') scroller.scrollTop = scroller.scrollHeight;
    else return;
    event.preventDefault();
    event.stopPropagation();
  }
}

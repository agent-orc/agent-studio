import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, afterNextRender, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthSessionState } from '../../../services/auth.service';
import { studioProjectSlug } from '../../../services/studio-project-slug.util';
import { replaceStudioRouteQuery } from '../../studio-shell/services/studio-route';
import { DisclosureMarkerComponent } from '../../../components/disclosure-marker/disclosure-marker.component';
import { SegmentedControlComponent } from '../../../components/segmented-control/segmented-control.component';
import { LAYOUT, layoutStructure, leafDetail, type LayoutBox } from '../structure-layout';
import { isGroup, kindText, observationText, projectStructure, summaryText, visibleBoxes,
  type StructureBox, type StructureData } from '../structure.model';

const STATUS: Record<string, { tone: 'ok' | 'warn' | 'down' | 'unknown'; word: string }> = {
  up: { tone: 'ok', word: 'Healthy' }, degraded: { tone: 'warn', word: 'Degraded' },
  down: { tone: 'down', word: 'Down' }, invalid: { tone: 'down', word: 'Check invalid' },
  unknown: { tone: 'unknown', word: 'Not observed yet' },
};
let instances = 0;

@Component({
  selector: 'app-structure-view',
  standalone: true,
  imports: [DisclosureMarkerComponent, SegmentedControlComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './structure-view.component.html',
  styleUrl: './structure-view.component.scss',
})
export class StructureViewComponent {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  readonly projectName = input<string | null>(null);
  readonly projectSlug = studioProjectSlug;
  readonly embedded = input(false);
  readonly hostOnly = input(false);
  readonly data = signal<StructureData>({ nodes: [], links: [] });
  readonly viewOptions = [{ value: 'host', label: 'Host' }, { value: 'container', label: 'Container' }] as const;
  readonly levelOptions = [{ value: 1, label: '1' }, { value: 2, label: '2' }, { value: 3, label: '3' }] as const;
  readonly error = signal<string | null>(null);
  readonly view = signal<'host' | 'container'>('host');
  readonly level = signal<1 | 2 | 3>(2);
  readonly toggles = signal<Record<string, boolean>>({});
  readonly selected = signal<string | null>(null);
  readonly overlay = signal<'health' | 'version' | 'drift' | 'load'>('health');
  readonly environment = signal('');
  readonly environments = computed(() => this.data().nodes.filter(node => node.kind === 'environment'));
  readonly selectedNode = computed(() => this.data().nodes.find(node => node.id === this.selected()) ?? null);
  readonly selectedLinks = computed(() => this.data().links.filter(link => link.from === this.selected() || link.to === this.selected()));
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
  private readGeneration = 0;

  ownerDescription(box: StructureBox): string {
    return box.otherProject ? `other project · ${box.ownerLabel ?? box.project}` : kindText(box);
  }
  /** Second line of a box: instances say how they are observed or placed, groups their kind. */
  detail(item: LayoutBox): string {
    return item.opaque && !isGroup(item.box) ? leafDetail(item.box, this.view()) : this.ownerDescription(item.box);
  }
  /** Calls and artifact flows carry an arrow; tunnels and transports are symmetric. */
  arrowFor(kind: string): string | null {
    return kind === 'call' || kind === 'artifact' || kind === 'publish' ? `url(#${this.arrowId})` : null;
  }
  status(box: StructureBox): { tone: string; word: string } | null {
    return box.status ? STATUS[box.status] ?? STATUS['unknown'] : null;
  }
  accessibleLabel(box: StructureBox): string {
    const observed = observationText(box);
    return `${box.label}, ${kindText(box)}` +
      (observed ? `, declared, ${observed}` : '') +
      (this.status(box) ? `, ${this.status(box)!.word}` : '') +
      (box.replicas > 1 ? `, ${box.replicas} replicas` : '') +
      (box.placements > 1 ? `, ${box.placements} placements` : '') +
      (box.otherProject ? `, other project, ${box.ownerLabel ?? box.project}` : '') +
      (box.collapsed && box.children.length ? `, ${summaryText(box)}` : '');
  }

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const main = this.element.nativeElement.querySelector<HTMLElement>('.structure-main');
      main?.focus();
      if (!main) return;
      this.measureWidth(main);
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(() => this.measureWidth(main));
      observer.observe(main);
      destroyRef.onDestroy(() => observer.disconnect());
    });
    effect(() => {
      this.projectName();
      this.auth.status();
      this.embedded();
      this.hostOnly();
      untracked(() => {
        const query = new URLSearchParams(this.embedded() ? '' : location.hash.split('?')[1] ?? '');
        try {
          const saved = JSON.parse(localStorage.getItem(this.preferenceKey()) || '{}') as
            { view?: 'host' | 'container'; level?: 1 | 2 | 3; overlay?: 'health' | 'version' | 'drift' | 'load' };
          if (saved.view === 'host' || saved.view === 'container') this.view.set(saved.view);
          if (saved.level === 1 || saved.level === 2 || saved.level === 3) this.level.set(saved.level);
          if (saved.overlay && ['health', 'version', 'drift', 'load'].includes(saved.overlay)) this.overlay.set(saved.overlay);
        } catch { /* Optional preference. */ }
        if (query.get('view') === 'host' || query.get('view') === 'container') this.view.set(query.get('view') as 'host' | 'container');
        if (['1', '2', '3'].includes(query.get('level') ?? '')) this.level.set(Number(query.get('level')) as 1 | 2 | 3);
        if (['health', 'version', 'drift', 'load'].includes(query.get('overlay') ?? '')) this.overlay.set(query.get('overlay') as 'health' | 'version' | 'drift' | 'load');
        this.environment.set(query.get('env') ?? '');
        this.selected.set(query.get('focus'));
        if (this.hostOnly()) this.view.set('host');
        void this.load();
      });
    });
  }

  private measureWidth(main: HTMLElement): void {
    const style = getComputedStyle(main);
    const width = main.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0');
    if (width > 0) this.canvasWidth.set(Math.max(480, Math.floor(width)));
  }

  private async load(): Promise<void> {
    const generation = ++this.readGeneration;
    try {
      const data = await firstValueFrom(this.http.get<StructureData>('/api/v1/operations/structure',
        { params: { scope: this.projectName() ? `project:${this.projectName()}` : 'workspace', ...(this.environment() ? { environment: this.environment() } : {}) } }));
      if (generation !== this.readGeneration) return;
      this.data.set(data);
      this.error.set(null);
    } catch { if (generation === this.readGeneration) this.error.set('Structure declarations could not be loaded.'); }
  }

  setView(view: 'host' | 'container'): void {
    if (this.hostOnly()) return;
    this.view.set(view);
    this.remember();
  }
  setOverlay(value: 'health' | 'version' | 'drift' | 'load'): void { this.overlay.set(value); this.remember(); }
  setEnvironment(value: string): void { this.environment.set(value); this.remember(); void this.load(); }
  setLevel(level: 1 | 2 | 3): void {
    this.level.set(level);
    this.remember();
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
    if (!this.embedded()) replaceStudioRouteQuery({ view: this.view(), level: String(this.level()), overlay: this.overlay(), env: this.environment(), focus: this.selected() });
    try { localStorage.setItem(this.preferenceKey(), JSON.stringify({ view: this.view(), level: this.level(), overlay: this.overlay() })); }
    catch { /* Preference storage is optional. */ }
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.altKey || event.ctrlKey || event.metaKey ||
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

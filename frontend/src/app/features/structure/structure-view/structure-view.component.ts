import { ChangeDetectionStrategy, Component, ElementRef, HostListener, afterNextRender, computed, effect, inject, signal } from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { DisclosureMarkerComponent } from '../../../components/disclosure-marker/disclosure-marker.component';
import { SegmentedControlComponent } from '../../../components/segmented-control/segmented-control.component';
import { nearestVisible, projectStructure, summaryText, visibleBoxes,
  type StructureBox, type StructureData, type StructureLink } from '../structure.model';

interface DrawnLink { index: number; path: string; x: number; y: number; kind: string; label: string; }
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

@Component({
  selector: 'app-structure-view',
  standalone: true,
  imports: [DatePipe, NgTemplateOutlet, DisclosureMarkerComponent, SegmentedControlComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './structure-view.component.html',
  styleUrl: './structure-view.component.scss',
})
export class StructureViewComponent {
  private readonly http = inject(HttpClient);
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  readonly data = signal<StructureData>({ nodes: [], links: [] });
  readonly viewOptions = [{ value: 'host', label: 'Host' }, { value: 'container', label: 'Container' }] as const;
  readonly levelOptions = [{ value: 1, label: '1' }, { value: 2, label: '2' }, { value: 3, label: '3' }] as const;
  readonly error = signal<string | null>(null);
  readonly library = signal<StructureLibrary>({ registries: [], consumers: [] });
  readonly libraryError = signal<string | null>(null);
  readonly refreshing = signal(false);
  readonly view = signal<'host' | 'container'>('host');
  readonly level = signal<1 | 2 | 3>(2);
  readonly toggles = signal<Record<string, boolean>>({});
  readonly selected = signal<string | null>(null);
  readonly roots = computed(() => projectStructure(this.data(), this.view(), this.level(), this.toggles()));
  readonly visible = computed(() => visibleBoxes(this.roots()));
  readonly drawnLinks = signal<DrawnLink[]>([]);
  readonly summaryText = summaryText;
  private frame = 0;
  private readonly linkRefresh = effect(() => {
    this.roots();
    if (typeof requestAnimationFrame !== 'undefined') {
      cancelAnimationFrame(this.frame);
      this.frame = requestAnimationFrame(() => this.measureLinks());
    }
  });

  constructor() {
    afterNextRender(() => this.element.nativeElement.querySelector<HTMLElement>('.structure-main')?.focus());
    try {
      const saved = JSON.parse(localStorage.getItem('studio.structure.workspace') || '{}') as
        { view?: 'host' | 'container'; level?: 1 | 2 | 3 };
      if (saved.view === 'host' || saved.view === 'container') this.view.set(saved.view);
      if (saved.level === 1 || saved.level === 2 || saved.level === 3) this.level.set(saved.level);
    } catch { /* Optional preference. */ }
    void this.load();
    void this.loadLibrary(false);
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

  private async load(): Promise<void> {
    try {
      this.data.set(await firstValueFrom(this.http.get<StructureData>('/api/v1/operations/structure',
        { params: { scope: 'workspace' } })));
      this.error.set(null);
    } catch { this.error.set('Structure declarations could not be loaded.'); }
  }

  setView(view: 'host' | 'container'): void {
    this.view.set(view);
    this.remember();
  }
  setLevel(level: 1 | 2 | 3): void {
    this.level.set(level);
    this.remember();
  }
  toggle(box: StructureBox): void {
    this.selected.set(box.id);
    if (box.children.length) this.toggles.update(current => ({ ...current, [box.id]: !box.collapsed }));
  }
  focus(id: string): void {
    this.selected.set(id);
    this.element.nativeElement.querySelector<HTMLElement>(`[data-node-id="${CSS.escape(id)}"]`)?.focus();
  }
  private remember(): void {
    try { localStorage.setItem('studio.structure.workspace', JSON.stringify({ view: this.view(), level: this.level() })); }
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
    else if (key === 'escape') this.selected.set(null);
    else return;
    event.preventDefault();
  }

  @HostListener('window:resize') onResize(): void { this.measureLinks(); }

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

  private measureLinks(): void {
    const canvas = this.element.nativeElement.querySelector<HTMLElement>('.structure-canvas');
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const visible = new Set(this.visible().map(node => node.id));
    const source = new Map(this.data().nodes.map(node => [node.id, node]));
    const boxes = new Map<string, DOMRect>();
    canvas.querySelectorAll<HTMLElement>('[data-node-id]').forEach(node =>
      boxes.set(node.dataset['nodeId']!, (node.closest('.structure-box') ?? node).getBoundingClientRect()));
    const endpoint = (id: string): DOMRect | undefined => {
      if (this.view() === 'container') {
        const node = source.get(id);
        if (node?.role && boxes.has(`function:${node.role}`)) return boxes.get(`function:${node.role}`);
        const descendant = this.data().nodes.find(candidate => {
          let parent = candidate.parentId;
          while (parent) {
            if (parent === id) return candidate.role && boxes.has(`function:${candidate.role}`);
            parent = source.get(parent)?.parentId ?? null;
          }
          return false;
        });
        if (descendant?.role) return boxes.get(`function:${descendant.role}`);
      } else {
        let node = source.get(id);
        while (node) {
          if (node.id.startsWith('machine:') ||
              ['physical', 'cloud-server', 'vm', 'cluster', 'namespace', 'host', 'server'].includes(node.kind)) {
            const box = boxes.get(node.id);
            if (box) return box;
          }
          node = node.parentId ? source.get(node.parentId) : undefined;
        }
      }
      const nearest = nearestVisible(id, visible, source);
      if (nearest && boxes.has(nearest)) return boxes.get(nearest);
      const child = this.data().nodes.find(node => node.parentId === id && boxes.has(node.id));
      return child ? boxes.get(child.id) : undefined;
    };
    const drawn: DrawnLink[] = [];
    this.data().links.forEach((link: StructureLink, index: number) => {
      const from = endpoint(link.from), to = endpoint(link.to);
      if (!from || !to || from === to) return;
      const localX = (x: number) => x - rect.left + canvas.scrollLeft;
      const localY = (y: number) => y - rect.top + canvas.scrollTop;
      let path: string, x: number, y: number;
      const horizontalGap = from.right <= to.left ? to.left - from.right :
        to.right <= from.left ? from.left - to.right : Number.POSITIVE_INFINITY;
      const verticalGap = from.bottom <= to.top ? to.top - from.bottom :
        to.bottom <= from.top ? from.top - to.bottom : Number.POSITIVE_INFINITY;
      if (horizontalGap <= verticalGap) {
        const forward = from.right <= to.left;
        const x1 = localX(forward ? from.right : from.left), x2 = localX(forward ? to.left : to.right);
        const y1 = localY(from.top + from.height / 2), y2 = localY(to.top + to.height / 2);
        const middle = (x1 + x2) / 2;
        path = `M${x1} ${y1} H${middle} V${y2} H${x2}`;
        x = middle; y = (y1 + y2) / 2;
      } else {
        const downward = from.bottom <= to.top;
        const x1 = localX(from.left + from.width / 2), x2 = localX(to.left + to.width / 2);
        const y1 = localY(downward ? from.bottom : from.top);
        const y2 = localY(downward ? to.top : to.bottom);
        const middle = (y1 + y2) / 2;
        path = `M${x1} ${y1} V${middle} H${x2} V${y2}`;
        x = (x1 + x2) / 2; y = middle;
      }
      drawn.push({ index: index + 1, kind: link.kind, label: link.label,
        path, x, y });
    });
    this.drawnLinks.set(drawn);
  }
}

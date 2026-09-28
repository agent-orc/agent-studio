import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiFolderChild, WikiFolderOverview, WikiNodeType } from '../../../../../models/project-docs.model';
import { ProjectDocsService } from '../../../../../services/project-docs.service';
import { WikiStarsService } from '../wiki-stars.service';
import { WikiClassBadge, classificationBadges } from '../wiki-classification';

const FILE_ORDER_DRAG_TYPE = 'application/x-wiki-file-order';

/** What the parent needs to open a page from a folder-overview row. */
export interface WikiFolderOpenRequest {
  relPath: string;
  type: WikiNodeType;
}

/** One clickable breadcrumb segment of the folder path. */
interface WikiFolderCrumb {
  label: string;
  relPath: string;
  current: boolean;
}

/**
 * Folder overview page: selecting a folder *name* in the wiki tree renders
 * this surface in the content pane (the chevron still only expands). It shows
 * a clickable breadcrumb and the folder's direct children as a table
 * (Titel | Datei | Typ | Geaendert | Groesse), folders first. Row clicks
 * drill into subfolders or open pages; navigation intent is emitted, the
 * parent owns routing. Fetches its own overview from the agreed
 * `GET /wiki/folder/{relPath}` contract whenever the inputs change.
 */
@Component({
  selector: 'app-wiki-folder-view',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-folder-view.component.html',
  styleUrl: './wiki-folder-view.component.scss',
})
export class WikiFolderViewComponent {
  readonly projectName = input.required<string>();
  readonly relPath = input.required<string>();
  /**
   * Monotonic re-read signal from the parent. Bumping it (after a wiki mutation)
   * re-fetches this overview in place - the current rows stay on screen and are
   * swapped only when the fresh data arrives - so an edit/delete/create under
   * the shown folder is reflected without navigating away and back.
   */
  readonly reloadNonce = input<number>(0);
  /** Current tree order for this folder, including optimistic reorders. */
  readonly orderedFileNames = input<readonly string[]>([]);
  /** Branch-backed wiki snapshots are read-only and expose no reorder affordance. */
  readonly writable = input(true);

  /** Drill into a subfolder's overview. */
  readonly openFolder = output<string>();
  /** Open a page in the reader. */
  readonly openPage = output<WikiFolderOpenRequest>();
  /** Root breadcrumb: back to the wiki landing view. */
  readonly openRoot = output<void>();
  /** Breadcrumb-end copy icon: request a shareable link to this folder. */
  readonly copyLink = output<string>();
  /** Optimistically reordered document names for persistence by the parent. */
  readonly fileOrderChange = output<string[]>();

  readonly overview = signal<WikiFolderOverview | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly draggingFileRel = signal<string | null>(null);
  readonly dropTargetRel = signal<string | null>(null);

  private readonly docs = inject(ProjectDocsService);
  private readonly stars = inject(WikiStarsService);

  /** The folder currently reflected on screen; distinguishes navigation from an in-place re-read. */
  private loadedKey: string | null = null;

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      const rel = this.relPath();
      this.reloadNonce(); // track: a parent bump re-reads the overview in place
      const key = `${project}::${rel}`;
      const navigated = key !== this.loadedKey;
      this.loadedKey = key;
      this.error.set(null);
      if (!project || !rel) {
        this.overview.set(null);
        this.loading.set(false);
        return;
      }
      // Navigating to a different folder resets to the loading placeholder; an
      // in-place re-read (same folder, bumped nonce) keeps the current rows on
      // screen and swaps them only once the fresh overview arrives.
      if (navigated) {
        this.overview.set(null);
        this.loading.set(true);
      }
      const subscription = this.docs.getWikiFolder(project, rel).subscribe({
        next: overview => {
          this.overview.set(overview);
          this.loading.set(false);
        },
        error: () => {
          this.error.set('Ordner-Übersicht konnte nicht geladen werden.');
          this.overview.set(null);
          this.loading.set(false);
        },
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  /** Direct children, folders first (stable within each group). */
  readonly children = computed<WikiFolderChild[]>(() => {
    const children = this.overview()?.children ?? [];
    const order = new Map(this.orderedFileNames().map((name, index) => [name, index]));
    return [
      ...children.filter(child => child.kind === 'folder'),
      ...children.filter(child => child.kind !== 'folder')
        .map((child, index) => ({ child, index }))
        .sort((a, b) => {
          const aOrder = order.get(a.child.name);
          const bOrder = order.get(b.child.name);
          if (aOrder == null && bOrder == null) return a.index - b.index;
          if (aOrder == null) return 1;
          if (bOrder == null) return -1;
          return aOrder - bOrder;
        })
        .map(item => item.child),
    ];
  });

  /** Breadcrumb segments (root first, current folder last). */
  readonly crumbs = computed<WikiFolderCrumb[]>(() => {
    const segments = this.relPath().split('/').filter(Boolean);
    return segments.map((segment, index) => ({
      label: segment,
      relPath: segments.slice(0, index + 1).join('/'),
      current: index === segments.length - 1,
    }));
  });

  readonly title = computed(() => this.overview()?.name || this.crumbs().at(-1)?.label || this.relPath());

  onRowClick(child: WikiFolderChild): void {
    if (child.kind === 'folder') {
      this.openFolder.emit(child.relPath);
      return;
    }
    this.openPage.emit({ relPath: child.relPath, type: child.fileType === 'html' ? 'html' : 'md' });
  }

  /** Star state of a page row (reactive: the template read tracks the store signal). */
  isStarred(child: WikiFolderChild): boolean {
    return child.kind !== 'folder' && this.stars.isStarred(this.projectName(), child.relPath);
  }

  /** Star toggle on a page row; stops propagation so the row click never opens the page. */
  toggleStar(event: Event, child: WikiFolderChild): void {
    event.stopPropagation();
    this.stars.toggle(this.projectName(), child.relPath, child.title || child.name);
  }

  onFileDragStart(event: DragEvent, child: WikiFolderChild): void {
    event.stopPropagation();
    if (!this.writable() || child.kind === 'folder' || !event.dataTransfer) return;
    event.dataTransfer.setData(FILE_ORDER_DRAG_TYPE, child.relPath);
    event.dataTransfer.setData('text/plain', child.relPath);
    event.dataTransfer.effectAllowed = 'move';
    this.draggingFileRel.set(child.relPath);
  }

  onFileDragOver(event: DragEvent, target: WikiFolderChild): void {
    if (!this.writable()) return;
    const draggedRel = this.draggingFileRel();
    if (!draggedRel || target.kind === 'folder' || draggedRel === target.relPath) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    this.dropTargetRel.set(target.relPath);
  }

  onFileDragLeave(target: WikiFolderChild): void {
    if (this.dropTargetRel() === target.relPath) this.dropTargetRel.set(null);
  }

  onFileDrop(event: DragEvent, target: WikiFolderChild): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.writable()) return;
    const draggedRel = event.dataTransfer?.getData(FILE_ORDER_DRAG_TYPE) || this.draggingFileRel();
    this.draggingFileRel.set(null);
    this.dropTargetRel.set(null);
    if (!draggedRel || target.kind === 'folder' || draggedRel === target.relPath) return;

    const current = this.overview();
    if (!current) return;
    const visibleChildren = this.children();
    const folders = visibleChildren.filter(child => child.kind === 'folder');
    const pages = visibleChildren.filter(child => child.kind !== 'folder');
    const from = pages.findIndex(child => child.relPath === draggedRel);
    const to = pages.findIndex(child => child.relPath === target.relPath);
    if (from < 0 || to < 0 || from === to) return;
    const [dragged] = pages.splice(from, 1);
    pages.splice(to, 0, dragged);
    this.overview.set({ ...current, children: [...folders, ...pages] });
    this.fileOrderChange.emit(pages.map(page => page.name));
  }

  onFileDragEnd(): void {
    this.draggingFileRel.set(null);
    this.dropTargetRel.set(null);
  }

  stopRowClick(event: Event): void {
    event.stopPropagation();
  }

  typeLabel(child: WikiFolderChild): string {
    if (child.kind === 'folder') return 'Ordner';
    return child.fileType ?? 'md';
  }

  /** Status/Typ cell: curated classification badges (pages only, often empty). */
  classBadges(child: WikiFolderChild): WikiClassBadge[] {
    return classificationBadges(child.classification);
  }

  /** Groesse cell: entry count for folders, human-readable bytes for pages. */
  sizeLabel(child: WikiFolderChild): string {
    if (child.kind === 'folder') {
      const count = child.childCount ?? 0;
      return count === 1 ? '1 Eintrag' : `${count} Einträge`;
    }
    return this.humanSize(child.size);
  }

  private humanSize(size: number | null): string {
    if (size == null || !Number.isFinite(size) || size < 0) return '–';
    if (size < 1024) return `${size} B`;
    const kb = size / 1024;
    if (kb < 1024) return `${kb < 10 ? kb.toFixed(1) : Math.round(kb)} KB`;
    const mb = kb / 1024;
    return `${mb < 10 ? mb.toFixed(1) : Math.round(mb)} MB`;
  }

  /** Compact relative time ("3h ago"), falling back to a locale date. */
  relativeTime(iso: string | null): string {
    if (!iso) return '–';
    const then = new Date(iso);
    const ms = then.getTime();
    if (Number.isNaN(ms)) return iso;
    const diff = Date.now() - ms;
    if (diff < 0) return then.toLocaleDateString('en-US');
    const min = Math.floor(diff / 60000);
    if (min < 1) return 'just now';
    if (min < 60) return `${min}m ago`;
    const hours = Math.floor(min / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 30) return `${days}d ago`;
    return then.toLocaleDateString('en-US');
  }

  absoluteTime(iso: string | null): string {
    if (!iso) return 'No timestamp available';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString('en-US');
  }
}

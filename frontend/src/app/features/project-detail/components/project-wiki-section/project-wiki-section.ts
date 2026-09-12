import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  HostListener,
  viewChildren,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { DriftReport, DriftReportDetailResponse } from '../../../../models/drift.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { DriftService } from '../../../../services/drift.service';
import { TaskService } from '../../../../services/task.service';
import { CliCatalogStore } from '../../../cli';
import { NotificationService } from '../../../../services/notification.service';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { CLI_TYPES, CliType, TaskState } from '../../../../models/task.model';
import {
  WikiFileSaveResult,
  WikiFileHistory,
  WikiGradingRunStatus,
  WikiNodeType,
  WikiPulse,
  RelatedTaskReference,
  WikiSearchResponse,
  WikiSearchResult,
  WikiTree,
  WikiTreeNode,
  WorkbenchListItem,
} from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MenuComponent } from '../../../../components/menu/menu.component';
import { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import {
  PageType,
  derivePageType,
  pageTypeIcon,
} from '../../../../models/page-context.model';
import { resolveWikiImageSrc } from '../../../../services/wiki-image-resolver';
import { WikiDashboardComponent } from './wiki-dashboard/wiki-dashboard.component';
import { WikiAgentReadsComponent } from './wiki-agent-reads/wiki-agent-reads.component';
import { WikiDocHistoryComponent } from './wiki-doc-history/wiki-doc-history.component';
import { WikiFolderViewComponent } from './wiki-folder-view/wiki-folder-view.component';
import { WikiPulseOpenRequest } from './wiki-pulse/wiki-pulse.component';
import { WikiSearchResultsComponent } from './wiki-search-results/wiki-search-results.component';
import { WikiSourceBadgeComponent } from './wiki-source-badge/wiki-source-badge.component';
import { WikiLinkedElementsComponent } from './wiki-linked-elements/wiki-linked-elements.component';
import {
  WikiTreeRow,
  collectDocumentPaths,
  collectDirectDocumentNames,
  collectFolderIds,
  filterWikiTree,
  flattenWikiTree,
  nodeId,
  planWikiSiblingReorder,
  reorderWikiFiles,
} from './wiki-tree';
import { WikiStarsService } from './wiki-stars.service';
import {
  WikiDeepLinkTarget,
  buildWikiRouteHash,
  buildWikiRouteUrl,
  isWikiRouteHash,
  parseWikiRouteHash,
  toProjectSlug,
} from './wiki-deep-link';
import { WikiMetricTone, documentMetricChips, driftChip } from './wiki-metric-chips';
import { WikiClassMeta, classificationBadges, classificationMeta } from './wiki-classification';
import { withRouteSegment } from '../../../../services/url-hash.util';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import {
  WikiLinkedElement,
  extractWikiLinkedElements,
  resolveWikiPageTarget,
  wikiLinkedElementKindLabel,
} from './wiki-linked-element';
import { WikiMetaPanelStateService } from './wiki-meta-panel-state.service';
import { WikiMetaSectionComponent } from './wiki-meta-section/wiki-meta-section.component';
import { WikiPageActionsComponent } from './wiki-page-actions/wiki-page-actions';
import { WikiLiveRefreshService } from '../../services/wiki-live-refresh.service';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  resolveIsolatedHtmlNavigation,
} from '../../../../services/sandboxed-html.util';

const FILE_DRAG_TYPE = 'application/x-wiki-file';
const FOLDER_DRAG_TYPE = 'application/x-wiki-folder';
const WIKI_STATE_STORAGE_PREFIX = 'atp.projectWiki.v1.';
const WIKI_NAV_MIN_WIDTH = 216;
const WIKI_NAV_MAX_WIDTH = 420;
const WIKI_NAV_DEFAULT_WIDTH = 286;
const WIKI_CONTEXT_MIN_WIDTH = 232;
const WIKI_CONTEXT_MAX_WIDTH = 420;
const WIKI_CONTEXT_DEFAULT_WIDTH = 284;
const WIKI_RESIZE_STEP = 16;
type WikiViewerTab = 'doc' | 'report' | 'source' | 'edit';
type WikiResizablePanel = 'nav' | 'context';
interface WikiPersistedState {
  navCollapsed?: boolean;
  openedRel?: string | null;
  viewerTab?: WikiViewerTab;
  navWidth?: number;
  contextWidth?: number;
  expandedIds?: string[];
}
interface WikiResizeState {
  panel: WikiResizablePanel;
  pointerId: number;
  startX: number;
  startWidth: number;
}

const WIKI_SEARCH_DEBOUNCE_MS = 300;
const WIKI_SEARCH_MIN_LENGTH = 2;

/**
 * Project-level knowledge view backed by the physical docs/ folder hierarchy:
 * the tree is the real folders + .md/.html files on disk (no virtual
 * organisation layer). Categories expand/collapse; the right pane renders the
 * selected page (markdown inline, HTML inside a script-enabled, opaque-origin
 * sandboxed iframe). The right context rail carries provenance, the file's git log, and
 * old-revision previews so only one page is open at a time.
 *
 * Structural edits are real git commits in the project repo: a text-only
 * context menu offers New page / New category / Rename / Delete, and dragging a
 * file onto a folder moves it (git mv). Dragging a folder onto a sibling
 * folder reorders the categories; the order persists server-side
 * (docs/app/config/wiki-order.json) through the same commit-backed mutation channel. The
 * tree re-reads from disk after every mutation, so what you see is the
 * committed state. A pinned "Overview" node above the categories reopens the
 * dashboard landing (the initial no-selection state).
 */
@Component({
  selector: 'app-project-wiki-section',
  standalone: true,
  imports: [
    FormsModule,
    MarkdownViewComponent,
    MarkdownRichEditorComponent,
    MenuComponent,
    OverlayPortalDirective,
    StudioIconComponent,
    TooltipDirective,
    AppTooltipDirective,
    WikiDashboardComponent,
    WikiAgentReadsComponent,
    WikiDocHistoryComponent,
    WikiFolderViewComponent,
    WikiMetaSectionComponent,
    WikiLinkedElementsComponent,
    WikiPageActionsComponent,
    WikiSearchResultsComponent,
    WikiSourceBadgeComponent,
  ],
  providers: [WikiLiveRefreshService, WikiMetaPanelStateService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-wiki-section.html',
  styleUrl: './project-wiki-section.scss',
})
export class ProjectWikiSectionComponent implements OnDestroy {
  readonly projectName = input.required<string>();
  readonly projectId = input<string | null>(null);
  /** Shell-owned target when this Wiki is mounted in a Studio editor tab. */
  readonly studioTabTarget = input<WikiDeepLinkTarget | null>(null);
  /** Legacy overlays navigate locally; Studio delegates targets to shell tab policy. */
  readonly openInternalTargetsInTabs = input(false);
  readonly openWikiTarget = output<{ target: WikiDeepLinkTarget; reuse: 'replace-current' | 'new' }>();
  readonly openWorkbench = output<WorkbenchListItem>();

  private readonly docs = inject(ProjectDocsService);
  private readonly stars = inject(WikiStarsService);
  private readonly drift = inject(DriftService);
  private readonly tasks = inject(TaskService);
  private readonly catalog = inject(CliCatalogStore);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly notifications = inject(NotificationService);
  private readonly publicDemo = inject(PublicDemoModeService);
  private readonly taskNavigation = inject(TaskReferenceNavigationService);
  private readonly metaPanelState = inject(WikiMetaPanelStateService);
  private readonly wikiLiveRefresh = inject(WikiLiveRefreshService);

  readonly cliTypes = CLI_TYPES;

  readonly tree = signal<WikiTree | null>(null);
  readonly pulse = signal<WikiPulse | null>(null);
  readonly pulseLoading = signal(false);

  // ---- Wiki grading maintenance run (AGT-2051) ----
  // Model chosen at the trigger, defaulting from the workspace maintenance model.
  readonly gradeCli = signal<CliType>('claude');
  readonly gradeModel = signal<string | null>(null);
  readonly gradeLevel = signal<string | null>(null);
  readonly gradingStatus = signal<WikiGradingRunStatus | null>(null);
  readonly gradeModelOptions = computed(() => this.catalog.modelsFor(this.gradeCli()));
  private gradingPollTimer: ReturnType<typeof setTimeout> | null = null;
  readonly loading = signal(false);
  readonly busy = signal(false);
  // Bumped after every tree re-read so a *mounted* folder-overview re-fetches
  // its own contents in place. The folder view self-fetches on projectName /
  // relPath only, so without this an in-place edit/delete/create under the
  // shown folder would leave its overview table stale (a soft refresh no longer
  // remounts it via the loading placeholder).
  readonly folderReloadNonce = signal(0);
  readonly filter = signal('');
  readonly filterOpen = signal(false);

  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly focusedRowId = signal<string | null>(null);
  readonly navCollapsed = signal(false);
  // One global preference covers the landing and every document. Page
  // navigation must never alter it.
  readonly contextCollapsed = this.metaPanelState.collapsed;
  readonly navWidth = signal(WIKI_NAV_DEFAULT_WIDTH);
  readonly contextWidth = signal(WIKI_CONTEXT_DEFAULT_WIDTH);
  readonly navWidthStyle = computed(() => `${this.navWidth()}px`);
  readonly contextWidthStyle = computed(() => `${this.contextWidth()}px`);
  readonly resizingPanel = signal<WikiResizablePanel | null>(null);

  readonly navMinWidth = WIKI_NAV_MIN_WIDTH;
  readonly navMaxWidth = WIKI_NAV_MAX_WIDTH;
  readonly contextMinWidth = WIKI_CONTEXT_MIN_WIDTH;
  readonly contextMaxWidth = WIKI_CONTEXT_MAX_WIDTH;

  readonly openedRel = signal<string | null>(null);
  readonly openedType = signal<WikiNodeType>('md');
  /** Distraction-free reading mode: the document fills the viewport, chrome hidden. */
  readonly maximized = signal(false);

  @HostListener('document:keydown.escape')
  exitReadingMode(): void {
    if (this.maximized()) this.maximized.set(false);
  }
  readonly openedContent = signal<string>('');
  readonly loadingDoc = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly viewerTab = signal<WikiViewerTab>('doc');
  readonly reportContent = signal('');
  readonly reportAnchor = signal<string | null>(null);
  readonly reportError = signal<string | null>(null);
  readonly loadingReport = signal(false);
  readonly saveBusy = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly saveResult = signal<WikiFileSaveResult | null>(null);
  readonly history = signal<WikiFileHistory | null>(null);
  readonly loadingHistory = signal(false);
  readonly pageUpdated = signal(false);
  readonly pageReloading = signal(false);

  // Folder overview: selecting a folder *name* in the tree shows its overview
  // page in the content pane (an open page always wins over the selection).
  readonly selectedFolderRel = signal<string | null>(null);

  // Wiki search (lexical, debounced; optional semantic expansion on demand).
  readonly searchQuery = signal('');
  readonly searchResponse = signal<WikiSearchResponse | null>(null);
  readonly searchLoading = signal(false);
  readonly searchError = signal<string | null>(null);
  readonly semanticLoading = signal(false);
  readonly semanticRequested = signal(false);
  /** The content pane switches to the result list from 2 characters on. */
  readonly searchActive = computed(() =>
    this.searchQuery().trim().length >= WIKI_SEARCH_MIN_LENGTH);
  private searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;
  /** Monotonic guard so a stale (slower) response never overwrites a newer one. */
  private searchSeq = 0;

  // Old-revision preview: when a sha is set, the doc pane shows that revision's
  // content instead of the working-tree content, with a "back to current" banner.
  readonly revisionSha = signal<string | null>(null);
  readonly revisionContent = signal<string>('');

  // Context menu + inline rename.
  readonly menuOpen = signal(false);
  readonly menuPos = signal<{ x: number; y: number } | null>(null);
  readonly menuTarget = signal<WikiTreeNode | null>(null);
  readonly renamingId = signal<string | null>(null);
  readonly renameValue = signal('');

  readonly draggingRel = signal<string | null>(null);
  readonly draggingFolderRel = signal<string | null>(null);
  readonly dropTargetId = signal<string | null>(null);

  readonly driftModalOpen = signal(false);
  readonly driftBusy = signal(false);
  readonly driftError = signal<string | null>(null);
  readonly driftMessage = signal<string | null>(null);
  readonly driftPrompt = signal('');
  readonly driftPromptLoading = signal(false);
  readonly driftReportDetail = signal<DriftReportDetailResponse | null>(null);
  readonly driftReports = signal<DriftReport[]>([]);
  readonly driftProjectKey = signal<string | null>(null);
  readonly driftWatchPath = signal<string | null>(null);
  readonly driftCli = signal<CliType>('claude');
  readonly driftModel = signal('');
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly htmlFrames = viewChildren<ElementRef<HTMLIFrameElement>>('wikiHtmlFrame');
  protected readonly wikiDocumentFrame = computed(() => this.htmlFrames()[0]?.nativeElement ?? null);
  private pendingOpenRestore: { rel: string; tab: WikiViewerTab } | null = null;
  private loadedReportPath: string | null = null;
  private resizeState: WikiResizeState | null = null;

  private readonly routeProjectRef = computed(() =>
    this.projectId()?.trim() || toProjectSlug(this.projectName()));
  /**
   * Deep-link target captured from the URL when the project is (re)bound, held
   * until the tree finishes loading so it can open the exact page/folder. A URL
   * param wins over the persisted localStorage open; absence falls back to it.
   */
  private pendingUrlTarget: WikiDeepLinkTarget | null = null;
  /**
   * True while a page/folder is being opened as a restore (URL deep-link,
   * localStorage, or browser back/forward). Suppresses the extra history push a
   * user-initiated open makes, so an auto-restore never litters the back stack.
   */
  private restoringOpen = false;
  /**
   * Subtle hint shown when a deep-linked path is not found in the tree. Carries
   * the target kind so the wording matches a missing page vs. a missing folder.
   */
  readonly deepLinkMissing = signal<{ relPath: string; kind: 'page' | 'folder' } | null>(null);
  private readonly onHashChange = (): void => this.applyHashTarget();

  protected readonly nodeId = nodeId;
  protected readonly documentMetricChips = documentMetricChips;
  protected readonly classificationBadges = classificationBadges;

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) {
        // Capture the shareable URL target (if any) before the tree loads so
        // restorePendingOpen can prefer it over the persisted localStorage open.
        this.pendingUrlTarget = this.captureUrlRestoreTarget();
        this.restorePersistedState(p);
        this.refresh();
        // Seed the grading trigger (maintenance-model default + current run
        // status) once per project. Kept out of refresh() so post-mutation
        // re-reads do not re-fire it.
        this.loadGradingContext();
      }
    });
    effect(() => {
      const target = this.studioTabTarget();
      if (!target) return;
      const tree = this.tree();
      if (!tree) {
        this.pendingUrlTarget = target;
        return;
      }
      if (this.sameWikiTarget(target, this.currentDeepLinkTarget())) return;
      this.applyDeepLinkTarget(tree, target);
    });
    if (typeof window !== 'undefined') {
      window.addEventListener('hashchange', this.onHashChange);
    }
  }

  @HostListener('window:message', ['$event'])
  onHtmlFrameMessage(event: MessageEvent): void {
    if (!this.htmlFrames().some(frame => event.source === frame.nativeElement.contentWindow)) return;
    const message = event.data as {
      type?: unknown; href?: unknown; button?: unknown; ctrlKey?: unknown; metaKey?: unknown;
    } | null;
    if (message?.type !== ISOLATED_HTML_LINK_MESSAGE || typeof message.href !== 'string') return;
    const openedRel = this.openedRel();
    if (!openedRel) return;
    const navigation = resolveIsolatedHtmlNavigation(`docs/${openedRel}`, message.href);
    if (navigation?.kind === 'external') {
      window.open(navigation.url, '_blank', 'noopener,noreferrer');
      return;
    }
    if (navigation?.kind !== 'wiki') return;
    const node = this.findNode(this.roots(), navigation.relPath);
    if (!node || node.type === 'folder' || !node.relPath) return;
    this.openFile(node.relPath, node.type, 'doc', null,
      message.button === 1 || message.ctrlKey === true || message.metaKey === true ? 'new' : 'replace-current');
  }

  ngOnDestroy(): void {
    if (typeof window !== 'undefined') {
      window.removeEventListener('hashchange', this.onHashChange);
    }
  }

  readonly roots = computed<WikiTreeNode[]>(() => this.tree()?.root ?? []);
  readonly wikiDocumentOrder = computed<string[]>(() => collectDocumentPaths(this.roots()));
  readonly selectedFolderDocumentOrder = computed<string[]>(() => {
    const rel = this.selectedFolderRel();
    return rel ? collectDirectDocumentNames(this.roots(), rel) : [];
  });

  readonly filteredRoots = computed<WikiTreeNode[]>(() =>
    filterWikiTree(this.roots(), this.filter()));

  readonly rows = computed<WikiTreeRow[]>(() => {
    const roots = this.filteredRoots();
    const exp = this.filter().trim() ? new Set(collectFolderIds(roots)) : this.expanded();
    return flattenWikiTree(roots, exp);
  });

  readonly docCount = computed(() => {
    let n = 0;
    const walk = (nodes: readonly WikiTreeNode[]): void => {
      for (const node of nodes) {
        if (node.type === 'folder') walk(node.children);
        else n++;
      }
    };
    walk(this.roots());
    return n;
  });

  readonly menuItems = computed<MenuItem[]>(() => {
    const t = this.menuTarget();
    if (!t) return [];
    if (!this.wikiWritable())
      return t.type === 'folder'
        ? [{ kind: 'row', id: 'open-new-tab', label: 'Open in new tab' }]
        : [
            { kind: 'row', id: 'open-new-tab', label: 'Open in new tab' },
            { kind: 'row', id: 'history', label: 'View history' },
          ];
    if (t.type === 'folder') {
      return [
        { kind: 'row', id: 'open-new-tab', label: 'Open in new tab' },
        { kind: 'row', id: 'new-page', label: 'New page' },
        { kind: 'row', id: 'new-folder', label: 'New category' },
        { kind: 'row', id: 'copy-link', label: 'Link kopieren' },
        { kind: 'row', id: 'rename', label: 'Rename' },
        { kind: 'separator' },
        { kind: 'row', id: 'delete', label: 'Delete category', danger: true },
      ];
    }
    return [
      { kind: 'row', id: 'open-new-tab', label: 'Open in new tab' },
      { kind: 'row', id: 'copy-link', label: 'Link kopieren' },
      { kind: 'row', id: 'rename', label: 'Rename' },
      { kind: 'row', id: 'history', label: 'View history' },
      { kind: 'separator' },
      { kind: 'row', id: 'delete', label: 'Delete', danger: true },
    ];
  });

  /** Image resolver bound to the currently opened doc's folder (markdown editor and HTML iframe). */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const project = this.projectName();
    const rel = this.openedRel();
    if (!rel) return (s: string) => s;
    return (s: string) => resolveWikiImageSrc(s, rel, a => this.docs.wikiAssetUrl(project, a));
  });

  /** Image resolver bound to the opened report doc's own folder, e.g. `assets/` siblings. */
  readonly reportImageResolver = computed<(src: string) => string>(() => {
    const project = this.projectName();
    const rel = this.reportPath();
    if (!rel) return (s: string) => s;
    return (s: string) => resolveWikiImageSrc(s, rel, a => this.docs.wikiAssetUrl(project, a));
  });

  /** Content shown in the doc pane: the previewed revision when one is active. */
  readonly displayContent = computed(() =>
    this.revisionSha() ? this.revisionContent() : this.openedContent());

  /** `allow-scripts` enables interaction; omitted same-origin isolates Studio state and APIs. */
  readonly trustedHtml = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(
      buildIsolatedHtmlSrcdoc(this.displayContent(), { resolveAssetSrc: this.imageResolver() })));

  readonly trustedReportHtml = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(this.reportHtmlForAnchor(
      buildIsolatedHtmlSrcdoc(this.reportContent(), { resolveAssetSrc: this.reportImageResolver() }),
      this.reportAnchor())));

  /** Pretty JSON preview for metadata files; invalid JSON falls back to source. */
  readonly displayJson = computed(() => this.formatJson(this.displayContent()));

  /**
   * Latest commit for the open doc (history is newest-first), surfaced as the
   * doc-header "last modified" line: when + who + the commit subject (= why).
   */
  readonly lastCommit = computed(() => this.history()?.commits?.[0] ?? null);

  readonly openedNode = computed(() => {
    const rel = this.openedRel();
    return rel ? this.findNode(this.roots(), rel) : null;
  });

  readonly reportPath = computed(() => this.openedNode()?.metadata?.reportPath ?? null);

  readonly openedTitle = computed(() =>
    this.openedNode()?.title ?? this.basename(this.openedRel() ?? 'Document'));

  readonly openedFolder = computed(() => {
    const rel = this.openedRel();
    if (!rel) return '';
    return this.parentDir(rel) || 'root folder';
  });

  readonly openedKindLabel = computed(() => {
    switch (this.openedType()) {
      case 'html': return 'HTML';
      case 'json': return 'JSON';
      case 'md': return 'Markdown';
      default: return 'Page';
    }
  });

  readonly canEditDoc = computed(() =>
    this.openedType() === 'md' && !this.revisionSha() && this.wikiWritable());
  readonly wikiWritable = computed(() => !this.publicDemo.readOnly() && this.tree()?.source?.writable !== false);
  readonly editDisabledReason = computed(() => {
    if (this.revisionSha()) return 'Old revisions are read-only.';
    if (!this.wikiWritable())
      return `This Wiki is read-only because its source is ${this.tree()?.source?.branch ?? 'a git branch'}, not the checkout.`;
    if (this.openedType() !== 'md') return 'Rich editing is currently available for Markdown pages.';
    return null;
  });

  readonly wordCount = computed(() => {
    const raw = this.displayContent()
      .replace(/```[\s\S]*?```/g, ' ')
      .replace(/<[^>]+>/g, ' ')
      .replace(/[#>*_`[\]()!-]/g, ' ');
    return raw.trim() ? raw.trim().split(/\s+/).length : 0;
  });

  readonly docLinks = computed<WikiLinkedElement[]>(() => extractWikiLinkedElements(this.displayContent()));
  readonly linkedTaskReferences = computed<RelatedTaskReference[]>(() => {
    const history = this.history();
    const related = [...(history?.relatedTasks ?? [])];
    const taskKey = history?.metadata.taskKey?.trim();
    if (taskKey && !related.some(item => item.key.toUpperCase() === taskKey.toUpperCase())) {
      related.unshift({
        key: taskKey,
        title: `Source task ${taskKey}`,
        linkedAt: history?.metadata.updatedAt ?? '',
        source: 'auto',
        exists: null,
      });
    }
    return related;
  });
  readonly linkedElementCount = computed(() => this.docLinks().length + this.linkedTaskReferences().length);
  protected readonly linkKindLabel = wikiLinkedElementKindLabel;

  /**
   * Compact drift grade for the open page, surfaced in the meta rail's toggle
   * head so the Wiki-grading verdict reads at a glance even while the rail is
   * collapsed. Derived from the same companion metadata that feeds the tree's
   * drift chip; null when the page has no companion metadata yet.
   */
  readonly metaGradeBadge = computed<{ display: string; label: string; tone: WikiMetricTone } | null>(() => {
    const meta = this.openedNode()?.metadata;
    if (!meta) return null;
    const chip = driftChip(meta);
    return { display: chip.display, label: chip.label, tone: chip.tone };
  });

  /**
   * "Klassifikation" block for the open page's meta rail, resolved from the
   * already-loaded tree node (relPath lookup, no extra HTTP): the status chip
   * in tree optics, the spelled-out type, the analysis date, and the successor
   * link when the page is superseded. Null hides the block entirely.
   */
  readonly openedClassification = computed<WikiClassMeta | null>(() =>
    classificationMeta(this.openedNode()?.classification));

  readonly registeredWorkbenchPaths = computed<ReadonlySet<string>>(() =>
    new Set((this.pulse()?.workbenches?.items ?? []).map(item => item.entryPath)));

  pageIcon(node: WikiTreeNode): StudioIconName {
    return pageTypeIcon(this.pageType(node));
  }

  pageType(node: WikiTreeNode): PageType {
    return derivePageType(
      node.relPath ?? '',
      node.classification,
      this.registeredWorkbenchPaths(),
    );
  }

  /** Successor link in the classification block: opens the superseding page. */
  openSupersededBy(rel: string): void {
    this.openFile(rel, this.wikiTypeForRel(rel));
  }

  readonly firstDoc = computed(() => this.findFirstDoc(this.roots()));

  // ---- stars (favourite documents) ----

  /** Mount guard for the landing "Gestarrt" panel (it renders itself from the store). */
  readonly hasStarred = computed(() => this.stars.entries(this.projectName()).length > 0);

  /** Star state of the open page (drives the viewer-head toggle). */
  readonly openedStarred = computed(() => {
    const rel = this.openedRel();
    return !!rel && this.stars.isStarred(this.projectName(), rel);
  });

  /** Viewer-head star toggle; the label is the title at the starring moment. */
  toggleOpenedStar(event: Event): void {
    event.stopPropagation();
    const rel = this.openedRel();
    if (!rel) return;
    this.stars.toggle(this.projectName(), rel, this.openedTitle());
  }

  onPulseOpen(req: WikiPulseOpenRequest): void {
    this.openFile(req.relPath, req.type);
  }

  // ---- folder overview (content-pane folder page) ----

  /** Tree folder *name* click: select the folder and show its overview page. */
  selectFolder(node: WikiTreeNode): void {
    if (node.type !== 'folder' || !node.relPath) return;
    this.openFolderOverview(node.relPath);
  }

  openTreeTarget(event: MouseEvent, node: WikiTreeNode): void {
    const reuse = event.ctrlKey || event.metaKey || event.button === 1 ? 'new' : 'replace-current';
    if (event.button === 1) event.preventDefault();
    if (node.type === 'folder') this.openFolderOverview(node.relPath ?? '', false, reuse);
    else if (node.relPath) this.openFile(node.relPath, node.type, 'doc', null, reuse);
  }

  /**
   * Shows a folder's overview page in the content pane (also used to drill
   * into subfolders from the overview table and breadcrumb). The folder is
   * expanded in the tree so the selection stays visible.
   */
  openFolderOverview(relPath: string, inPlace = false, reuse: 'replace-current' | 'new' = 'replace-current'): void {
    const target: WikiDeepLinkTarget = relPath
      ? { kind: 'folder', relPath }
      : { kind: 'overview' };
    if (!inPlace && this.requestStudioTab(target, reuse)) return;
    this.resetSearchState();
    this.deepLinkMissing.set(null);
    if (this.openedRel()) this.closeFile(inPlace);
    if (!relPath) {
      this.selectedFolderRel.set(null);
      this.syncDeepLinkUrl('replace');
      return;
    }
    this.expandAncestors(relPath);
    this.expand(relPath);
    this.focusedRowId.set(relPath);
    this.selectedFolderRel.set(relPath);
    // Folder navigation is pure state-sync (reachable by tree clicks), so it
    // replaces rather than growing the history stack per click.
    this.syncDeepLinkUrl('replace');
  }

  /** Root breadcrumb of the folder overview: back to the Pulse landing. */
  showWikiLanding(): void {
    if (this.requestStudioTab({ kind: 'overview' })) return;
    this.selectedFolderRel.set(null);
    this.deepLinkMissing.set(null);
    this.syncDeepLinkUrl('replace');
  }

  // ---- Overview node (pinned tree entry for the dashboard landing) ----

  /** The pinned "Overview" tree node lights up whenever the landing is on screen. */
  readonly overviewActive = computed(() =>
    !this.searchActive() && !this.openedRel() && !this.selectedFolderRel());

  /**
   * Pinned "Overview" node at the top of the tree: drops every selection
   * (open page, folder overview, search) so the dashboard landing renders -
   * the same state as the initial view.
   */
  openOverview(inPlace = false): void {
    if (!inPlace && this.requestStudioTab({ kind: 'overview' })) return;
    this.resetSearchState();
    this.deepLinkMissing.set(null);
    if (this.openedRel()) this.closeFile(inPlace);
    this.selectedFolderRel.set(null);
    this.syncDeepLinkUrl('replace');
  }

  // ---- wiki search (debounced lexical, semantic expansion on demand) ----

  onSearchQueryChange(value: string): void {
    this.searchQuery.set(value);
    if (this.searchDebounceTimer) clearTimeout(this.searchDebounceTimer);
    this.searchDebounceTimer = null;
    this.searchSeq++; // invalidate any in-flight response for the old query
    this.semanticRequested.set(false);
    this.searchError.set(null);
    const query = value.trim();
    if (query.length < WIKI_SEARCH_MIN_LENGTH) {
      this.searchResponse.set(null);
      this.searchLoading.set(false);
      this.semanticLoading.set(false);
      return;
    }
    this.searchLoading.set(true);
    this.searchDebounceTimer = setTimeout(() => {
      this.searchDebounceTimer = null;
      this.runWikiSearch(query, false);
    }, WIKI_SEARCH_DEBOUNCE_MS);
  }

  /** "Semantisch erweitern": re-run the current query with semantic=true. */
  expandSearchSemantically(): void {
    const query = this.searchQuery().trim();
    if (query.length < WIKI_SEARCH_MIN_LENGTH || this.semanticLoading()) return;
    this.semanticRequested.set(true);
    this.runWikiSearch(query, true);
  }

  /** Enter in the search box: open the top hit. */
  openTopSearchResult(): void {
    const top = this.searchResponse()?.results?.[0];
    if (top && this.searchActive()) this.openSearchResult(top);
  }

  openSearchResult(result: WikiSearchResult): void {
    this.openFile(result.relPath, this.wikiTypeForRel(result.relPath));
  }

  /** Esc / clearing the box: drop the search, the previous view reappears. */
  clearSearch(): void {
    this.resetSearchState();
  }

  private resetSearchState(): void {
    if (this.searchDebounceTimer) clearTimeout(this.searchDebounceTimer);
    this.searchDebounceTimer = null;
    this.searchSeq++;
    this.searchQuery.set('');
    this.searchResponse.set(null);
    this.searchLoading.set(false);
    this.searchError.set(null);
    this.semanticLoading.set(false);
    this.semanticRequested.set(false);
  }

  private runWikiSearch(query: string, semantic: boolean): void {
    const seq = ++this.searchSeq;
    if (semantic) this.semanticLoading.set(true);
    else this.searchLoading.set(true);
    this.searchError.set(null);
    this.docs.searchWiki(this.projectName(), query, { semantic }).subscribe({
      next: response => {
        if (seq !== this.searchSeq) return;
        this.searchResponse.set(response);
        this.searchLoading.set(false);
        this.semanticLoading.set(false);
      },
      error: () => {
        if (seq !== this.searchSeq) return;
        this.searchLoading.set(false);
        this.semanticLoading.set(false);
        this.searchError.set('Suche fehlgeschlagen.');
      },
    });
  }

  /** Open a critical page (from the grade panel) straight to its report tab. */
  openReportPage(relPath: string): void {
    this.openFile(relPath, this.wikiTypeForRel(relPath), 'report');
  }

  private wikiTypeForRel(relPath: string): WikiNodeType {
    const ext = relPath.toLowerCase().split('.').pop() ?? '';
    if (ext === 'html' || ext === 'htm') return 'html';
    if (ext === 'json') return 'json';
    return 'md';
  }

  // ---- wiki grading trigger (AGT-2051) ----

  private gradingSeededFor: string | null = null;

  onGradeModelChange(model: string): void {
    this.gradeModel.set(model || null);
  }

  onGradeLevelChange(level: string | null): void {
    this.gradeLevel.set(level);
  }

  startWikiGrading(): void {
    const p = this.projectName();
    if (!p) return;
    this.docs.startWikiGrading(p, {
      cliType: this.gradeCli(),
      model: this.gradeModel() ?? undefined,
      thinkingLevel: this.gradeLevel(),
    }).subscribe({
      next: status => { this.gradingStatus.set(status); this.scheduleGradingPoll(); },
      error: err => {
        // A 409 (a run already in flight) returns the live status in the body.
        const status = err?.error?.status as WikiGradingRunStatus | undefined;
        if (status) { this.gradingStatus.set(status); this.scheduleGradingPoll(); }
      },
    });
  }

  abortWikiGrading(): void {
    const p = this.projectName();
    if (!p) return;
    this.docs.abortWikiGrading(p).subscribe({
      next: resp => this.gradingStatus.set(resp.status),
      error: () => void 0,
    });
  }

  /**
   * Loads the maintenance-model default (once per project) to pre-fill the
   * trigger's model picker, then fetches the current run status so a run started
   * in another tab is reflected and resumed-polled here.
   */
  private loadGradingContext(): void {
    const p = this.projectName();
    if (!p) return;
    if (this.gradingSeededFor !== p) {
      this.gradingSeededFor = p;
      this.gradingStatus.set(null);
      this.docs.getMaintenanceModel().subscribe({
        next: cfg => {
          const cli = CLI_TYPES.includes(cfg.cliType as CliType) ? (cfg.cliType as CliType) : 'claude';
          this.gradeCli.set(cli);
          this.gradeModel.set(cfg.model || null);
          this.gradeLevel.set(cfg.thinkingLevel ?? null);
        },
        error: () => void 0,
      });
    }
    this.docs.getWikiGradingStatus(p).subscribe({
      next: resp => {
        this.gradingStatus.set(resp.status);
        if (resp.status?.state === 'running') this.scheduleGradingPoll();
      },
      error: () => void 0,
    });
  }

  /** Polls the run status while a run is in flight; refreshes Pulse when it ends. */
  private scheduleGradingPoll(): void {
    if (this.gradingPollTimer) return;
    this.gradingPollTimer = setTimeout(() => {
      this.gradingPollTimer = null;
      const p = this.projectName();
      if (!p) return;
      this.docs.getWikiGradingStatus(p).subscribe({
        next: resp => {
          this.gradingStatus.set(resp.status);
          if (resp.status?.state === 'running') this.scheduleGradingPoll();
          else this.refresh(); // run finished: refresh Pulse so new grades / critical pages show
        },
        error: () => void 0,
      });
    }, 1200);
  }

  readonly rootFolderLabel = computed(() => {
    const base = this.tree()?.baseDir?.trim();
    return base || 'root folder';
  });

  readonly modelOptions = computed(() => this.catalog.modelsFor(this.driftCli()));

  readonly selectedModelLabel = computed(() => {
    const id = this.driftModel();
    if (!id) return 'CLI default';
    return this.modelOptions().find(m => m.id === id)?.label ?? id;
  });

  readonly latestDriftReport = computed<DriftReport | null>(() =>
    this.driftReportDetail()?.report ?? this.driftReports()[0] ?? null);

  readonly driftModalText = computed(() => {
    const markdown = this.driftReportDetail()?.markdown;
    if (markdown?.trim()) return markdown;
    return this.buildDocumentDriftPrompt();
  });

  readonly driftCopyLabel = computed(() => {
    switch (this.copyState()) {
      case 'copied': return 'Copied';
      case 'failed': return 'Copy failed';
      default: return 'Copy result';
    }
  });

  // ---- loading ----

  refresh(options: { soft?: boolean; onLoaded?: (tree: WikiTree | null) => void } = {}): void {
    const p = this.projectName();
    if (!p) return;
    // A soft refresh (post-mutation re-read) swaps the tree data in place: the
    // rendered rows stay on screen, expand/selection/scroll state is untouched
    // (it lives in component signals, not in the fetched tree), and folders that
    // no longer exist simply drop out. The full-flush `loading` placeholder is
    // reserved for the initial project load, so an edit/delete/rename never
    // flashes the whole section back to "Loading...". The busy() header chip is
    // the only movement the user sees.
    if (options.soft !== true) this.loading.set(true);
    this.docs.getWikiTree(p).subscribe({
      next: t => {
        this.tree.set(t);
        this.restorePendingOpen(t);
        this.loading.set(false);
        // Nudge a mounted folder-overview to re-read its own contents in place,
        // then let the caller reconcile the shown surface against the fresh tree
        // (e.g. steer away from a page/folder the mutation removed or pruned).
        this.folderReloadNonce.update(n => n + 1);
        options.onLoaded?.(t);
      },
      error: () => {
        this.loading.set(false);
        options.onLoaded?.(null);
      },
    });
    // The Pulse landing view is git-backed and only shown when no page is open,
    // so it loads independently and never blocks the tree render. One composed
    // call (feed + inbox + drift) keeps the landing off the per-doc git fan-out.
    this.pulseLoading.set(true);
    this.docs.getWikiPulse(p, 12).subscribe({
      next: r => {
        this.pulse.set(r);
        this.pulseLoading.set(false);
      },
      error: () => {
        this.pulse.set(null);
        this.pulseLoading.set(false);
      },
    });
  }

  // ---- viewer ----

  openFile(rel: string, type: WikiNodeType = 'md', tab: WikiViewerTab = 'doc', reportAnchor: string | null = null,
    reuse: 'replace-current' | 'new' = 'replace-current'): void {
    if (this.requestStudioTab({ kind: 'page', relPath: rel }, reuse)) return;
    this.resetSearchState();
    this.deepLinkMissing.set(null);
    this.selectedFolderRel.set(null);
    this.expandAncestors(rel);
    this.focusedRowId.set(rel);
    this.openedRel.set(rel);
    this.openedType.set(type);
    this.viewerTab.set(tab);
    this.openedContent.set('');
    this.loadError.set(null);
    this.reportContent.set('');
    this.reportAnchor.set(reportAnchor);
    this.reportError.set(null);
    this.loadingReport.set(false);
    this.loadedReportPath = null;
    this.saveError.set(null);
    this.saveResult.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.pageUpdated.set(false);
    this.pageReloading.set(false);
    this.wikiLiveRefresh.watchPage(this.projectName(), rel, () => {
      if (this.openedRel() === rel) this.pageUpdated.set(true);
    });
    this.loadingDoc.set(true);
    this.docs.getWikiFile(this.projectName(), rel).subscribe({
      next: r => {
        this.openedContent.set(r.content);
        this.loadError.set(null);
        this.loadingDoc.set(false);
      },
      error: error => {
        this.openedContent.set('');
        this.loadError.set(error?.error?.error ?? `Page '${rel}' could not be loaded.`);
        this.loadingDoc.set(false);
      },
    });
    this.history.set(null);
    this.loadingHistory.set(true);
    this.docs.getWikiFileHistoryVersion(this.projectName(), rel).subscribe({
      next: response => {
        if (!response.body || this.openedRel() !== rel) return;
        this.history.set(response.body);
        this.wikiLiveRefresh.setPageVersion(response.etag);
        this.loadingHistory.set(false);
      },
      error: () => this.loadingHistory.set(false),
    });
    if (tab === 'report') this.loadReport();
    // A user-driven page open is a history entry; a restore (URL / storage /
    // back-forward) only syncs state and must not push a new entry.
    this.syncDeepLinkUrl(this.restoringOpen ? 'replace' : 'push');
    this.persistState();
  }

  closeFile(inPlace = false): void {
    if (!inPlace && this.requestStudioTab({ kind: 'overview' })) return;
    this.wikiLiveRefresh.stopPage();
    this.openedRel.set(null);
    this.openedContent.set('');
    this.reportContent.set('');
    this.reportAnchor.set(null);
    this.reportError.set(null);
    this.loadingReport.set(false);
    this.loadedReportPath = null;
    this.saveError.set(null);
    this.saveResult.set(null);
    this.history.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.pageUpdated.set(false);
    this.pageReloading.set(false);
    this.pendingOpenRestore = null;
    this.deepLinkMissing.set(null);
    this.syncDeepLinkUrl('replace');
    this.persistState();
  }

  selectTab(tab: WikiViewerTab): void {
    if (tab === 'edit' && !this.canEditDoc()) return;
    this.viewerTab.set(tab);
    if (tab === 'report') this.loadReport();
    this.persistState();
  }

  openReportSection(event: Event, node: WikiTreeNode, anchor: string | null): void {
    event.preventDefault();
    event.stopPropagation();
    if (!node.relPath || !anchor) return;
    this.reportAnchor.set(anchor);
    if (this.openedRel() === node.relPath) {
      this.viewerTab.set('report');
      this.loadReport();
      this.persistState();
      return;
    }
    this.openFile(node.relPath, node.type, 'report', anchor);
  }

  saveWikiContent(content: string): void {
    const rel = this.openedRel();
    if (!rel || this.openedType() !== 'md') return;
    this.saveBusy.set(true);
    this.saveError.set(null);
    this.saveResult.set(null);
    this.docs.putWikiFile(this.projectName(), rel, content).subscribe({
      next: result => {
        this.saveBusy.set(false);
        this.saveResult.set(result);
        this.openedContent.set(content);
        this.reloadHistory(rel);
        this.refresh();
      },
      error: err => {
        this.saveBusy.set(false);
        this.saveError.set(err?.error?.error ?? 'Saving failed.');
      },
    });
  }

  reloadUpdatedPage(): void {
    const rel = this.openedRel();
    if (!rel || this.pageReloading()) return;
    this.pageReloading.set(true);
    this.docs.getWikiFile(this.projectName(), rel).subscribe({
      next: response => {
        if (this.openedRel() !== rel) return;
        this.openedContent.set(response.content);
        this.loadError.set(null);
        this.revisionSha.set(null);
        this.revisionContent.set('');
        this.pageUpdated.set(false);
        this.pageReloading.set(false);
        this.reloadHistory(rel);
      },
      error: error => {
        if (this.openedRel() === rel) {
          this.loadError.set(error?.error?.error ?? `Page '${rel}' could not be reloaded.`);
          this.pageReloading.set(false);
        }
      },
    });
  }

  dismissPageUpdate(): void {
    this.pageUpdated.set(false);
  }

  private loadReport(): void {
    const path = this.reportPath();
    if (!path) {
      this.reportContent.set('');
      this.reportError.set('No reasoning report is linked to this document yet.');
      this.loadingReport.set(false);
      this.loadedReportPath = null;
      return;
    }
    if (this.loadedReportPath === path && this.reportContent()) return;

    this.loadedReportPath = path;
    this.reportContent.set('');
    this.reportError.set(null);
    this.loadingReport.set(true);
    this.docs.getWikiFile(this.projectName(), path).subscribe({
      next: r => {
        this.reportContent.set(r.content);
        this.loadingReport.set(false);
      },
      error: () => {
        this.reportError.set('Failed to load the linked reasoning report.');
        this.loadingReport.set(false);
      },
    });
  }

  private reloadHistory(rel: string): void {
    this.loadingHistory.set(true);
    this.docs.getWikiFileHistoryVersion(this.projectName(), rel).subscribe({
      next: response => {
        if (!response.body || this.openedRel() !== rel) return;
        this.history.set(response.body);
        this.wikiLiveRefresh.setPageVersion(response.etag);
        this.loadingHistory.set(false);
      },
      error: () => this.loadingHistory.set(false),
    });
  }

  openFirstDoc(): void {
    const first = this.firstDoc();
    if (first?.relPath) this.openFile(first.relPath, first.type);
  }

  toggleFilter(): void {
    this.filterOpen.update(v => !v);
    queueMicrotask(() => {
      const root = this.host.nativeElement as HTMLElement;
      root.querySelector<HTMLInputElement>('[data-testid="project-wiki-filter"]')?.focus();
    });
  }

  toggleNav(): void {
    this.navCollapsed.update(v => !v);
    this.persistState();
  }

  toggleContext(): void {
    this.metaPanelState.togglePanel();
  }

  private setContextCollapsed(collapsed: boolean): void {
    this.metaPanelState.setPanelCollapsed(collapsed);
  }

  readonly linkedElementHref = (link: WikiLinkedElement): string => {
    if (link.kind === 'external' || link.kind === 'anchor') return link.target;
    if (link.kind === 'task') return `#task:${link.taskReference ?? link.label}`;
    const rel = this.resolveLinkedWikiPage(link);
    return rel
      ? buildWikiRouteHash(this.routeProjectRef(), { kind: 'page', relPath: rel })
      : link.target;
  };

  openLinkedElement(request: { link: WikiLinkedElement; reuse: 'replace-current' | 'new' }): void {
    const link = request.link;
    if (link.kind === 'task') {
      const reference = link.taskReference ?? link.label;
      const match = this.taskNavigation.markdownReferences()
        .find(item => item.label.toUpperCase() === reference.toUpperCase());
      this.taskNavigation.openTaskKey(match?.taskKey ?? reference);
      return;
    }
    const rel = this.resolveLinkedWikiPage(link);
    if (rel) this.openFile(rel, this.wikiTypeForRel(rel), 'doc', null, request.reuse);
  }

  onRenderedWikiLink(event: MouseEvent): void {
    const anchor = event.composedPath().find((item): item is HTMLAnchorElement =>
      item instanceof HTMLAnchorElement && item.hasAttribute('href'));
    if (!anchor) return;
    const href = anchor.getAttribute('href');
    const openedRel = this.openedRel();
    if (!href || !openedRel || href.startsWith('#') || /^[a-z][a-z0-9+.-]*:/i.test(href)) return;
    const rel = resolveWikiPageTarget(href, openedRel);
    const node = rel ? this.findNode(this.roots(), rel) : null;
    if (!node || node.type === 'folder' || !node.relPath) return;
    if (event.type === 'auxclick' && event.button !== 1) return;
    event.preventDefault();
    this.openFile(node.relPath, node.type, 'doc', null,
      event.button === 1 || event.ctrlKey || event.metaKey ? 'new' : 'replace-current');
  }

  startPanelResize(event: PointerEvent, panel: WikiResizablePanel): void {
    event.preventDefault();
    const target = event.currentTarget as HTMLElement | null;
    target?.setPointerCapture?.(event.pointerId);
    this.resizeState = {
      panel,
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth: panel === 'nav' ? this.navWidth() : this.contextWidth(),
    };
    this.resizingPanel.set(panel);
  }

  resizePanel(event: PointerEvent): void {
    const state = this.resizeState;
    if (!state || event.pointerId !== state.pointerId) return;

    const delta = state.panel === 'nav'
      ? event.clientX - state.startX
      : state.startX - event.clientX;
    if (Math.abs(delta) <= 2) return;

    this.applyPanelWidth(state.panel, state.startWidth + delta);
  }

  finishPanelResize(event: PointerEvent): void {
    const state = this.resizeState;
    if (!state || event.pointerId !== state.pointerId) return;

    const target = event.currentTarget as HTMLElement | null;
    target?.releasePointerCapture?.(event.pointerId);
    this.resizeState = null;
    this.resizingPanel.set(null);
    this.persistState();
  }

  onPanelSplitterKeydown(event: KeyboardEvent, panel: WikiResizablePanel): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();

    const direction = event.key === 'ArrowRight' ? 1 : -1;
    const current = panel === 'nav' ? this.navWidth() : this.contextWidth();
    const next = panel === 'nav'
      ? current + (direction * WIKI_RESIZE_STEP)
      : current - (direction * WIKI_RESIZE_STEP);
    this.applyPanelWidth(panel, next);
    this.persistState();
  }

  onDriftCliChange(value: string): void {
    const next = CLI_TYPES.includes(value as CliType) ? value as CliType : 'claude';
    this.driftCli.set(next);
    this.driftModel.set('');
    this.catalog.ensure(next).subscribe({ error: () => void 0 });
  }

  onDriftModelChange(value: string): void {
    this.driftModel.set(value);
  }

  openDriftModal(): void {
    this.driftModalOpen.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);
    this.copyState.set('idle');
    this.catalog.ensure(this.driftCli()).subscribe({ error: () => void 0 });
    this.resolveDriftProjectContext(() => {
      this.loadDriftReports();
      this.loadDriftPrompt();
    });
  }

  closeDriftModal(ev?: Event): void {
    if (ev && ev.target && (ev.target as HTMLElement).closest('.pwiki__drift-card')) return;
    this.driftModalOpen.set(false);
  }

  stopModal(ev: Event): void {
    ev.stopPropagation();
  }

  prepareDriftEvidenceReport(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.driftBusy.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);
    this.drift.runSoftwareArchitectureDrift(project).subscribe({
      next: detail => {
        this.driftReportDetail.set(detail);
        this.driftReports.set([detail.report, ...this.driftReports().filter(r => r.reportId !== detail.report.reportId)]);
        this.driftBusy.set(false);
        this.driftMessage.set(`Evidence report ${detail.report.reportId} recorded.`);
      },
      error: err => {
        this.driftBusy.set(false);
        this.driftError.set(this.describeError(err, 'Could not prepare the drift evidence report.'));
      },
    });
  }

  startDriftCliTask(): void {
    const project = this.projectName();
    const watchPath = this.driftWatchPath();
    if (!project || !watchPath) {
      this.resolveDriftProjectContext(() => this.startDriftCliTask());
      return;
    }

    const cli = this.driftCli();
    const model = this.driftModel().trim();
    const docSlug = this.toSlug(this.openedRel() ?? 'wiki-index').slice(0, 36);
    const id = `wiki-drift-${docSlug}-${Date.now().toString(36)}`.slice(0, 96);
    this.driftBusy.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);

    this.tasks.createJob({
      id,
      title: `Knowledge drift: ${this.openedTitle()}`,
      agent: cli,
      cliType: cli,
      model: model || undefined,
      watchPath,
      targetState: TaskState.Ready,
      taskType: 'chore',
      promptMarkdown: this.buildDocumentDriftPrompt(),
    }).subscribe({
      next: created => {
        const jobId = created?.id ?? id;
        this.tasks.startJob(jobId, watchPath, model || undefined, cli).subscribe({
          next: () => {
            this.driftBusy.set(false);
            this.driftMessage.set(`Started ${jobId} with ${cli}${model ? ` / ${model}` : ''}.`);
          },
          error: err => {
            this.driftBusy.set(false);
            this.driftError.set(this.describeError(err, `Created ${jobId}, but could not start the CLI run.`));
          },
        });
      },
      error: err => {
        this.driftBusy.set(false);
        this.driftError.set(this.describeError(err, 'Could not create the drift CLI task.'));
      },
    });
  }

  copyDriftResult(): void {
    void copyTextToClipboard(this.driftModalText()).then(ok => {
      this.copyState.set(ok ? 'copied' : 'failed');
      if (this.copyResetTimer) clearTimeout(this.copyResetTimer);
      this.copyResetTimer = setTimeout(() => {
        this.copyState.set('idle');
        this.copyResetTimer = null;
      }, 1800);
    });
  }

  // ---- old-revision preview ----

  onViewRevision(sha: string): void {
    const rel = this.openedRel();
    if (!rel || !sha) return;
    this.docs.getWikiRevision(this.projectName(), sha, rel).subscribe({
      next: r => {
        this.revisionSha.set(sha);
        this.revisionContent.set(r.content);
        this.viewerTab.set('doc');
        this.persistState();
      },
      error: () => { /* leave the current view untouched on failure */ },
    });
  }

  backToCurrent(): void {
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.persistState();
  }

  // ---- tree expand/collapse ----

  toggleExpand(id: string): void {
    const next = new Set(this.expanded());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.expanded.set(next);
    this.focusedRowId.set(id);
    this.persistState();
  }

  private expand(id: string): void {
    if (this.expanded().has(id)) return;
    const next = new Set(this.expanded());
    next.add(id);
    this.expanded.set(next);
    this.persistState();
  }

  focusTreeRow(node: WikiTreeNode): void {
    this.focusedRowId.set(nodeId(node));
  }

  focusTreeRowElement(event: MouseEvent, node: WikiTreeNode): void {
    this.focusedRowId.set(nodeId(node));
    const row = event.currentTarget as HTMLElement | null;
    if (!row) return;
    queueMicrotask(() => row.focus());
  }

  rowTabIndex(row: WikiTreeRow, index: number): number {
    const id = nodeId(row.node);
    const focused = this.focusedRowId();
    if (focused) return focused === id ? 0 : -1;
    const active = this.openedRel();
    if (active && active === id) return 0;
    return index === 0 ? 0 : -1;
  }

  treeItemExpanded(row: WikiTreeRow): boolean | null {
    return row.node.type === 'folder' && row.hasChildren ? row.expanded : null;
  }

  onTreeKeydown(event: KeyboardEvent): void {
    const rows = this.rows();
    if (rows.length === 0) return;

    const currentIndex = this.currentTreeRowIndex(event, rows);
    const row = rows[currentIndex];
    if (!row) return;

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.focusRowAt(Math.min(currentIndex + 1, rows.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.focusRowAt(Math.max(currentIndex - 1, 0));
        break;
      case 'ArrowRight':
        event.preventDefault();
        if (row.node.type === 'folder' && row.hasChildren) {
          if (!row.expanded) {
            this.expand(nodeId(row.node));
            this.focusRowAt(currentIndex);
          } else {
            this.focusRowAt(Math.min(currentIndex + 1, this.rows().length - 1));
          }
        }
        break;
      case 'ArrowLeft':
        event.preventDefault();
        if (row.node.type === 'folder' && row.expanded) {
          this.toggleExpand(nodeId(row.node));
          this.focusRowAt(currentIndex);
        } else {
          const parent = this.parentRowIndex(rows, currentIndex);
          if (parent >= 0) this.focusRowAt(parent);
        }
        break;
      case 'Home':
        event.preventDefault();
        this.focusRowAt(0);
        break;
      case 'End':
        event.preventDefault();
        this.focusRowAt(rows.length - 1);
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.activateTreeRow(row);
        break;
    }
  }

  // ---- context menu ----

  onContextMenu(ev: MouseEvent, node: WikiTreeNode): void {
    ev.preventDefault();
    this.menuTarget.set(node);
    this.menuPos.set({ x: ev.clientX, y: ev.clientY });
    this.menuOpen.set(true);
  }

  closeMenu(): void {
    this.menuOpen.set(false);
    this.menuTarget.set(null);
  }

  onMenuItemClick(ev: MenuItemClickEvent): void {
    const t = this.menuTarget();
    if (!t) return;
    switch (ev.id) {
      case 'open-new-tab':
        if (t.relPath) {
          const target: WikiDeepLinkTarget = t.type === 'folder'
            ? { kind: 'folder', relPath: t.relPath }
            : { kind: 'page', relPath: t.relPath };
          this.requestStudioTab(target, 'new');
        }
        break;
      case 'rename':
        this.startRename(t);
        break;
      case 'history':
        if (t.relPath) {
          // Viewing history implies wanting the meta rail visible: open the
          // page, then force the globally remembered rail state to expanded.
          this.openFile(t.relPath, t.type);
          this.setContextCollapsed(false);
        }
        break;
      case 'new-page':
        this.promptNewPage(t.relPath ?? '');
        break;
      case 'new-folder':
        this.promptNewFolder(t.relPath ?? '');
        break;
      case 'copy-link':
        this.copyWikiLinkForNode(t);
        break;
      case 'delete':
        this.deleteNode(t);
        break;
    }
    this.closeMenu();
  }

  // ---- create page / folder ----

  promptNewPage(folderRel: string): void {
    if (!this.wikiWritable()) return;
    const name = this.prompt('New page name (e.g. guide.md):', '');
    if (name) this.createPage(folderRel, name);
  }

  promptNewFolder(folderRel: string): void {
    if (!this.wikiWritable()) return;
    const name = this.prompt('New category name:', '');
    if (name) this.createFolder(folderRel, name);
  }

  /** Creates a page under a folder, defaulting to .md when no extension is given. */
  createPage(folderRel: string, name: string): void {
    const trimmed = name.trim();
    if (!trimmed) return;
    const withExt = /\.(md|html?|htm)$/i.test(trimmed) ? trimmed : `${trimmed}.md`;
    const rel = this.joinRel(folderRel, withExt);
    this.runMutation(this.docs.createWikiPage(this.projectName(), rel));
  }

  createFolder(folderRel: string, name: string): void {
    const trimmed = name.trim();
    if (!trimmed) return;
    const rel = this.joinRel(folderRel, trimmed);
    this.runMutation(this.docs.createWikiFolder(this.projectName(), rel));
  }

  // ---- rename (inline) ----

  startRename(node: WikiTreeNode): void {
    this.renamingId.set(nodeId(node));
    this.renameValue.set(node.name);
    queueMicrotask(() => {
      const root = this.host.nativeElement as HTMLElement;
      const el = root.querySelector<HTMLInputElement>('.pwiki__rename-input');
      el?.focus();
      el?.select();
    });
  }

  commitRename(): void {
    const id = this.renamingId();
    if (!id) return;
    const name = this.renameValue().trim();
    this.renamingId.set(null);
    if (!name || name === this.basename(id)) return;
    const target = this.findNode(this.roots(), id);
    if (!target?.relPath) return;
    const newRel = this.joinRel(this.parentDir(target.relPath), name);
    this.runMutation(this.docs.moveWikiNode(this.projectName(), target.relPath, newRel));
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  // ---- delete ----

  private deleteNode(node: WikiTreeNode): void {
    if (!node.relPath) return;
    const ok = this.confirm(`Delete "${node.name}"? This is committed to the repository.`);
    if (!ok) return;
    const rel = node.relPath;
    this.runMutation(this.docs.deleteWikiNode(this.projectName(), rel), tree => {
      // Drop the now-dead path (and, for a folder, its descendants) from the
      // favourites store so the landing never renders a star for a gone page.
      this.stars.removeUnder(this.projectName(), rel);
      // Reconcile the shown surface against the freshly re-read tree.
      this.reconcileViewAfterDelete(tree);
      // The tree context menu (and its Delete) stays reachable while the
      // search-results pane is showing. When the reconcile did not already
      // clear the search by steering, re-run the active query so a deleted page
      // does not linger as a dead, clickable hit.
      if (this.searchActive()) this.runWikiSearch(this.searchQuery().trim(), this.semanticRequested());
    });
  }

  /**
   * Keep the content pane on a surface that still exists after a delete + soft
   * re-read. When the open page or the shown folder overview is gone from the
   * fresh tree - a direct delete, or a parent folder the backend pruned because
   * the delete emptied it - fall back to the nearest surviving ancestor folder
   * overview (rewriting the deep-link to ?folder=<ancestor>), or the dashboard
   * landing when none survives. A delete of some other, unviewed node matches
   * neither branch and leaves the current view exactly where it is; the folder
   * reload nonce refreshes a shown overview in place for that case.
   */
  private reconcileViewAfterDelete(tree: WikiTree | null): void {
    if (!tree) return;
    const roots = tree.root;
    const openRel = this.openedRel();
    if (openRel) {
      const node = this.findNode(roots, openRel);
      if (node && node.type !== 'folder') return; // open page still present
      this.steerToNearestFolder(roots, this.parentDir(openRel));
      return;
    }
    const folderRel = this.selectedFolderRel();
    if (folderRel) {
      const node = this.findNode(roots, folderRel);
      if (node && node.type === 'folder') return; // shown folder still present
      this.steerToNearestFolder(roots, this.parentDir(folderRel));
    }
  }

  /** Open the nearest ancestor folder that still exists, else the landing. */
  private steerToNearestFolder(roots: readonly WikiTreeNode[], startRel: string): void {
    let cur = startRel;
    while (cur) {
      const node = this.findNode(roots, cur);
      if (node && node.type === 'folder') {
        this.openFolderOverview(cur, true);
        return;
      }
      cur = this.parentDir(cur);
    }
    this.openOverview(true);
  }

  onNodeDragStart(ev: DragEvent, node: WikiTreeNode): void {
    if (!this.wikiWritable()) return;
    if (!node.relPath || !ev.dataTransfer) return;
    if (node.type === 'folder') {
      ev.dataTransfer.setData(FOLDER_DRAG_TYPE, node.relPath);
      ev.dataTransfer.setData('text/plain', node.relPath);
      ev.dataTransfer.effectAllowed = 'move';
      this.draggingFolderRel.set(node.relPath);
      return;
    }
    ev.dataTransfer.setData(FILE_DRAG_TYPE, node.relPath);
    ev.dataTransfer.setData('text/plain', node.relPath);
    ev.dataTransfer.effectAllowed = 'move';
    this.draggingRel.set(node.relPath);
  }

  onDragEnd(): void {
    this.draggingRel.set(null);
    this.draggingFolderRel.set(null);
    this.dropTargetId.set(null);
  }

  onNodeDragOver(ev: DragEvent, target: WikiTreeNode): void {
    if (!target.relPath) return;
    const draggingFolder = this.draggingFolderRel();
    if (draggingFolder) {
      if (target.type !== 'folder' || !planWikiSiblingReorder(
        this.roots(), draggingFolder, target.relPath, 'folder')) return;
    } else {
      const draggingFile = this.draggingRel();
      if (!draggingFile) return;
      if (target.type !== 'folder'
        && !planWikiSiblingReorder(this.roots(), draggingFile, target.relPath, 'file')) return;
    }
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'move';
    this.dropTargetId.set(nodeId(target));
  }

  onNodeDragLeave(target: WikiTreeNode): void {
    if (this.dropTargetId() === nodeId(target)) this.dropTargetId.set(null);
  }
  onNodeDrop(ev: DragEvent, target: WikiTreeNode): void {
    if (!target.relPath) return;
    ev.preventDefault();
    const folderRel = ev.dataTransfer?.getData(FOLDER_DRAG_TYPE) || this.draggingFolderRel();
    const fileRel = ev.dataTransfer?.getData(FILE_DRAG_TYPE) || this.draggingRel();
    this.dropTargetId.set(null);
    this.draggingRel.set(null);
    this.draggingFolderRel.set(null);
    if (folderRel) {
      const reorder = target.type === 'folder'
        ? planWikiSiblingReorder(this.roots(), folderRel, target.relPath, 'folder')
        : null;
      if (reorder) this.runMutation(this.docs.setWikiFolderOrder(
        this.projectName(), reorder.parentRel, reorder.orderedNames));
      return;
    }
    if (!fileRel) return;
    if (target.type !== 'folder') {
      const reorder = planWikiSiblingReorder(this.roots(), fileRel, target.relPath, 'file');
      if (reorder) this.persistFileOrder(reorder.parentRel, reorder.orderedNames);
      return;
    }
    const name = this.basename(fileRel);
    const dest = this.joinRel(target.relPath, name);
    if (dest === fileRel) return;
    this.runMutation(this.docs.moveWikiNode(this.projectName(), fileRel, dest));
  }

  private persistFileOrder(parentRel: string, orderedNames: string[]): void {
    const current = this.tree();
    if (current) this.tree.set({
      ...current,
      root: reorderWikiFiles(current.root, parentRel, orderedNames),
    });
    this.runMutation(this.docs.setWikiFileOrder(this.projectName(), parentRel, orderedNames));
  }

  runFileOrderMutation(parentRel: string, orderedNames: string[]): void {
    if (this.wikiWritable()) this.persistFileOrder(parentRel, orderedNames);
  }

  // ---- mutation plumbing ----

  private runMutation(
    obs: { subscribe: (o: { next: () => void; error: () => void }) => unknown },
    onSuccess?: (tree: WikiTree | null) => void,
  ): void {
    this.busy.set(true);
    obs.subscribe({
      next: () => {
        this.busy.set(false);
        // Reconcile *after* the soft re-read so the callback sees the fresh tree:
        // it can then steer away from a page/folder the mutation removed (or a
        // parent the backend pruned because the delete emptied it) instead of
        // landing on a stale or now-missing target.
        this.refresh({ soft: true, onLoaded: onSuccess });
      },
      error: () => {
        this.busy.set(false);
        this.refresh({ soft: true });
      },
    });
  }

  // ---- persisted workspace state ----

  private restorePersistedState(projectName: string): void {
    const state = this.readPersistedState(projectName);
    this.navCollapsed.set(state?.navCollapsed === true);
    this.metaPanelState.restore();
    this.navWidth.set(this.clampNavWidth(state?.navWidth ?? WIKI_NAV_DEFAULT_WIDTH));
    this.contextWidth.set(this.clampContextWidth(state?.contextWidth ?? WIKI_CONTEXT_DEFAULT_WIDTH));
    this.expanded.set(new Set(state?.expandedIds ?? []));
    this.focusedRowId.set(state?.openedRel ?? null);
    this.viewerTab.set(this.safeViewerTab(state?.viewerTab));
    this.openedRel.set(null);
    this.openedContent.set('');
    this.reportContent.set('');
    this.reportAnchor.set(null);
    this.reportError.set(null);
    this.loadingReport.set(false);
    this.loadedReportPath = null;
    this.saveError.set(null);
    this.saveResult.set(null);
    this.history.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.pendingOpenRestore = state?.openedRel
      ? { rel: state.openedRel, tab: this.safeViewerTab(state.viewerTab) }
      : null;
  }

  private restorePendingOpen(tree: WikiTree): void {
    // A shareable URL param wins over the persisted localStorage open.
    const urlTarget = this.pendingUrlTarget;
    this.pendingUrlTarget = null;
    if (urlTarget) {
      this.pendingOpenRestore = null;
      this.applyDeepLinkTarget(tree, urlTarget);
      return;
    }
    const pending = this.pendingOpenRestore;
    if (!pending) return;
    this.pendingOpenRestore = null;
    const node = this.findNode(tree.root, pending.rel);
    if (!node || node.type === 'folder' || !node.relPath) {
      this.persistState();
      return;
    }
    this.restoringOpen = true;
    try {
      this.openFile(node.relPath, node.type, pending.tab);
    } finally {
      this.restoringOpen = false;
    }
  }

  // ---- shareable deep links (URL <-> open page/folder) ----

  /**
   * Reconcile the wiki view with the URL hash on a browser back/forward (or an
   * external hash edit). Off-route hashes (e.g. a studio Hub tab) return null
   * and are ignored; a wiki route with no tree yet defers to restorePendingOpen.
   */
  private applyHashTarget(): void {
    const projectRef = this.routeProjectRef();
    if (!projectRef) return;
    const target = parseWikiRouteHash(window.location.hash, projectRef);
    if (!target) return;
    const tree = this.tree();
    if (!tree) {
      this.pendingUrlTarget = target;
      return;
    }
    this.applyDeepLinkTarget(tree, target);
  }

  /** Open the page/folder named by a deep-link target, tolerating stale paths. */
  private applyDeepLinkTarget(tree: WikiTree, target: WikiDeepLinkTarget): void {
    this.restoringOpen = true;
    try {
      if (target.kind === 'page') {
        const node = this.findNode(tree.root, target.relPath);
        if (!node || node.type === 'folder' || !node.relPath) {
          this.noteMissingDeepLink(target.relPath, 'page');
          return;
        }
        if (this.openedRel() === node.relPath) return;
        this.openFile(node.relPath, node.type);
      } else if (target.kind === 'folder') {
        const node = this.findNode(tree.root, target.relPath);
        if (!node || node.type !== 'folder' || !node.relPath) {
          this.noteMissingDeepLink(target.relPath, 'folder');
          return;
        }
        if (this.selectedFolderRel() === node.relPath && !this.openedRel()) return;
        this.openFolderOverview(node.relPath);
      } else {
        // Overview / landing: drop any open page or folder selection.
        this.deepLinkMissing.set(null);
        if (this.openedRel()) this.closeFile();
        else this.selectedFolderRel.set(null);
      }
    } finally {
      this.restoringOpen = false;
    }
  }

  /** Read the shareable target from the URL, ignoring the paramless landing. */
  private captureUrlRestoreTarget(): WikiDeepLinkTarget | null {
    if (typeof window === 'undefined') return null;
    const projectRef = this.routeProjectRef();
    if (!projectRef) return null;
    const target = parseWikiRouteHash(window.location.hash, projectRef);
    // No param (bare wiki route) leaves localStorage as the fallback.
    return target && target.kind !== 'overview' ? target : null;
  }

  /** The wiki target the URL should currently reflect. */
  private currentDeepLinkTarget(): WikiDeepLinkTarget {
    const page = this.openedRel();
    if (page) return { kind: 'page', relPath: page };
    const folder = this.selectedFolderRel();
    if (folder) return { kind: 'folder', relPath: folder };
    return { kind: 'overview' };
  }

  private requestStudioTab(target: WikiDeepLinkTarget, reuse: 'replace-current' | 'new' = 'replace-current'): boolean {
    if (!this.openInternalTargetsInTabs() || this.restoringOpen) return false;
    if (this.sameWikiTarget(target, this.currentDeepLinkTarget())) return false;
    this.openWikiTarget.emit({ target, reuse });
    return true;
  }

  private sameWikiTarget(left: WikiDeepLinkTarget, right: WikiDeepLinkTarget): boolean {
    if (left.kind !== right.kind) return false;
    if (left.kind === 'overview' || right.kind === 'overview') return true;
    return left.relPath === right.relPath;
  }

  /**
   * Write the open page/folder into the wiki rail hash. Only rewrites the URL
   * when the wiki rail route is the active hash (its deep-link surface) so the
   * component never hijacks the URL when mounted off-route (studio Hub tab).
   */
  private syncDeepLinkUrl(mode: 'push' | 'replace'): void {
    if (typeof window === 'undefined') return;
    const projectRef = this.routeProjectRef();
    if (!projectRef) return;
    if (!isWikiRouteHash(window.location.hash, projectRef)) return;
    // Write the target as the route segment so coexisting state such as
    // board filters survives page and folder navigation.
    const nextRoute = buildWikiRouteHash(projectRef, this.currentDeepLinkTarget()).slice(1);
    const nextHash = withRouteSegment(window.location.hash, nextRoute);
    if (window.location.hash === nextHash) return;
    const url = `${window.location.pathname}${window.location.search}${nextHash}`;
    try {
      window.history[mode === 'push' ? 'pushState' : 'replaceState'](null, '', url);
    } catch {
      /* history writes are best-effort; the view still works without them */
    }
  }

  /** Land on the wiki landing + a dezent hint, and drop the bad param. */
  private noteMissingDeepLink(relPath: string, kind: 'page' | 'folder'): void {
    this.deepLinkMissing.set({ relPath, kind });
    if (this.openedRel()) this.closeFile();
    else this.selectedFolderRel.set(null);
    this.syncDeepLinkUrl('replace');
  }

  /** Dismiss the "linked page not found" hint. */
  dismissDeepLinkHint(): void {
    this.deepLinkMissing.set(null);
  }

  /** Context-menu "Link kopieren" for a tree node (page or folder). */
  copyWikiLinkForNode(node: WikiTreeNode): void {
    if (!node.relPath) return;
    this.copyWikiLink(node.type === 'folder'
      ? { kind: 'folder', relPath: node.relPath }
      : { kind: 'page', relPath: node.relPath });
  }

  /** Viewer-header copy icon: link to the currently open page. */
  copyOpenedPageLink(): void {
    const rel = this.openedRel();
    if (rel) this.copyWikiLink({ kind: 'page', relPath: rel });
  }

  /** Folder-overview breadcrumb copy icon: link to the shown folder. */
  copyFolderLink(relPath: string): void {
    if (relPath) this.copyWikiLink({ kind: 'folder', relPath });
  }

  private copyWikiLink(target: WikiDeepLinkTarget): void {
    if (typeof window === 'undefined') return;
    const projectRef = this.routeProjectRef();
    if (!projectRef) return;
    const url = buildWikiRouteUrl(window.location, projectRef, target);
    void copyTextToClipboard(url).then(ok => {
      if (ok) this.notifications.success('Link kopiert', 'Wiki');
      else this.notifications.info('Link konnte nicht kopiert werden', 'Wiki');
    });
  }

  private persistState(): void {
    const projectName = this.projectName();
    if (!projectName) return;
    const state: WikiPersistedState = {
      navCollapsed: this.navCollapsed(),
      openedRel: this.openedRel(),
      viewerTab: this.viewerTab(),
      navWidth: this.navWidth(),
      contextWidth: this.contextWidth(),
      expandedIds: [...this.expanded()],
    };
    this.writePersistedState(projectName, state);
  }

  private readPersistedState(projectName: string): WikiPersistedState | null {
    try {
      const raw = globalThis.localStorage?.getItem(this.storageKey(projectName));
      if (!raw) return null;
      const parsed = JSON.parse(raw) as Partial<WikiPersistedState>;
      return {
        navCollapsed: parsed.navCollapsed === true,
        openedRel: typeof parsed.openedRel === 'string' && parsed.openedRel.trim() ? parsed.openedRel : null,
        viewerTab: this.safeViewerTab(parsed.viewerTab),
        navWidth: this.readStoredWidth(parsed.navWidth, WIKI_NAV_MIN_WIDTH, WIKI_NAV_MAX_WIDTH),
        contextWidth: this.readStoredWidth(parsed.contextWidth, WIKI_CONTEXT_MIN_WIDTH, WIKI_CONTEXT_MAX_WIDTH),
        expandedIds: this.readStoredExpandedIds(parsed.expandedIds),
      };
    } catch {
      return null;
    }
  }

  private writePersistedState(projectName: string, state: WikiPersistedState): void {
    try {
      globalThis.localStorage?.setItem(this.storageKey(projectName), JSON.stringify(state));
    } catch {
      /* persistence is a convenience; the wiki keeps working without storage */
    }
  }

  private storageKey(projectName: string): string {
    return `${WIKI_STATE_STORAGE_PREFIX}${encodeURIComponent(projectName)}`;
  }

  private safeViewerTab(value: unknown): WikiViewerTab {
    return value === 'source' || value === 'doc' || value === 'report' || value === 'edit'
      ? value
      : 'doc';
  }

  private reportHtmlForAnchor(html: string, anchor: string | null): string {
    const cleanAnchor = this.safeReportAnchor(anchor);
    if (!cleanAnchor || !html) return html;
    const injection = `<meta http-equiv="refresh" content="0;url=#${cleanAnchor}">`
      + `<style>:target{outline:2px solid #2563eb;outline-offset:6px;scroll-margin-top:18px;}</style>`;
    if (/<head[^>]*>/i.test(html)) {
      return html.replace(/<head([^>]*)>/i, `<head$1>${injection}`);
    }
    return injection + html;
  }

  private safeReportAnchor(anchor: string | null): string | null {
    if (!anchor) return null;
    const clean = anchor.trim().toLowerCase();
    return /^[a-z0-9-]+$/.test(clean) ? clean : null;
  }

  private applyPanelWidth(panel: WikiResizablePanel, width: number): void {
    if (panel === 'nav') {
      this.navWidth.set(this.clampNavWidth(width));
      return;
    }
    this.contextWidth.set(this.clampContextWidth(width));
  }

  private clampNavWidth(width: number): number {
    return this.clampWidth(width, WIKI_NAV_MIN_WIDTH, WIKI_NAV_MAX_WIDTH);
  }

  private clampContextWidth(width: number): number {
    return this.clampWidth(width, WIKI_CONTEXT_MIN_WIDTH, WIKI_CONTEXT_MAX_WIDTH);
  }

  private clampWidth(width: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, Math.round(width)));
  }

  private readStoredWidth(value: unknown, min: number, max: number): number | undefined {
    if (typeof value !== 'number' || !Number.isFinite(value)) return undefined;
    return this.clampWidth(value, min, max);
  }

  private readStoredExpandedIds(value: unknown): string[] | undefined {
    if (!Array.isArray(value)) return undefined;
    return value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0);
  }

  private resolveDriftProjectContext(after: () => void): void {
    const project = this.projectName();
    this.tasks.getWatchPaths().subscribe({
      next: entries => {
        const match = entries.find(e => e.name === project)
          ?? entries.find(e => e.path === project);
        this.driftWatchPath.set(match?.path ?? project);
        this.driftProjectKey.set(match?.path ? this.basename(match.path.replace(/\\/g, '/')) : project);
        after();
      },
      error: () => {
        this.driftWatchPath.set(project);
        this.driftProjectKey.set(project);
        after();
      },
    });
  }

  private loadDriftReports(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.drift.listReports(project, { limit: 12 }).subscribe({
      next: resp => this.driftReports.set(resp?.reports ?? []),
      error: () => this.driftReports.set([]),
    });
  }

  private loadDriftPrompt(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.driftPromptLoading.set(true);
    this.drift.getSoftwareArchitectureDriftPrompt(project).subscribe({
      next: resp => {
        this.driftPrompt.set(resp.prompt ?? '');
        this.driftPromptLoading.set(false);
      },
      error: err => {
        this.driftPrompt.set('');
        this.driftPromptLoading.set(false);
        this.driftError.set(this.describeError(err, 'Could not load the architecture drift prompt.'));
      },
    });
  }

  private buildDocumentDriftPrompt(): string {
    const rel = this.openedRel() ?? '(root folder)';
    const title = this.openedRel() ? this.openedTitle() : 'Root folder';
    const report = this.latestDriftReport();
    const basePrompt = this.driftPrompt().trim();
    const linked = this.docLinks().map(link => `- ${this.linkKindLabel(link.kind)}: ${link.label} (${link.target})`).join('\n');
    const model = this.driftModel().trim() || 'CLI default';

    return `# Knowledge page drift analysis: ${title}

Project: ${this.projectName()}
Page: ${rel}
Category: ${this.openedRel() ? this.openedFolder() : 'root folder'}
Page type: ${this.openedRel() ? this.openedKindLabel() : 'Root folder'}
Selected CLI: ${this.driftCli()}
Selected model: ${model}
Latest known drift report: ${report ? `${report.reportId} (${this.formatTimestamp(report.createdAt)})` : 'none loaded'}

## Objective

Evaluate whether the selected knowledge page still matches the current project architecture, source tree, task evidence, and concept notes.

## Linked elements visible in the Knowledge UI

${linked || '- No explicit Markdown or HTML links detected in the selected page.'}

## Required output

1. Produce a concise human-readable drift report.
2. Classify the result as Healthy, Watch, Warn, Critical, or Unknown.
3. List evidence refs using repository-relative paths.
4. Identify whether the page needs edits, a follow-up task, or no action.
5. Create or update a conceptual page-metadata note for this page. Suggested sidecar path:
   \`docs/.drift/${this.toSlug(rel)}.md\`
6. If the result should become a project drift report, post the structured response back through:
   \`POST /api/drift/{project}/actions/software-architecture-drift\`

## Base architecture-drift prompt

${basePrompt || '(Prompt not loaded yet. Use the project architecture model, docs, source tree, schemas, tests, recent tasks, and recent drift reports as evidence.)'}
`;
  }

  // ---- path + node helpers ----

  private parentDir(rel: string): string {
    const i = rel.lastIndexOf('/');
    return i >= 0 ? rel.slice(0, i) : '';
  }

  private basename(rel: string): string {
    const i = rel.lastIndexOf('/');
    return i >= 0 ? rel.slice(i + 1) : rel;
  }

  private joinRel(dir: string, name: string): string {
    return dir ? `${dir}/${name}` : name;
  }

  private findNode(nodes: readonly WikiTreeNode[], id: string): WikiTreeNode | null {
    for (const n of nodes) {
      if (nodeId(n) === id) return n;
      const hit = this.findNode(n.children, id);
      if (hit) return hit;
    }
    return null;
  }

  private findFirstDoc(nodes: readonly WikiTreeNode[]): WikiTreeNode | null {
    for (const node of nodes) {
      if (node.type !== 'folder') return node;
      const hit = this.findFirstDoc(node.children);
      if (hit) return hit;
    }
    return null;
  }

  private resolveLinkedWikiPage(link: WikiLinkedElement): string | null {
    const openedRel = this.openedRel();
    if (!openedRel) return null;
    const rel = resolveWikiPageTarget(link.target, openedRel);
    const node = rel ? this.findNode(this.roots(), rel) : null;
    return node && node.type !== 'folder' ? rel : null;
  }

  private expandAncestors(rel: string): void {
    const parts = rel.split('/');
    if (parts.length <= 1) return;
    const next = new Set(this.expanded());
    for (let i = 1; i < parts.length; i++) {
      next.add(parts.slice(0, i).join('/'));
    }
    this.expanded.set(next);
  }

  navPath(node: WikiTreeNode): string {
    const rel = node.relPath ?? '';
    if (!rel) return 'root folder';
    const parent = this.parentDir(rel);
    return parent || 'root folder';
  }

  fileTypeLabel(node: WikiTreeNode): string {
    switch (node.type) {
      case 'html': return 'HTML page';
      case 'json': return 'JSON metadata';
      default: return 'Markdown page';
    }
  }

  private toSlug(value: string): string {
    return value.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'document';
  }

  private describeError(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }

  // Seams so unit tests can drive the create/rename/delete flows without the
  // browser dialogs that back the menu UX.
  protected prompt(message: string, value: string): string | null {
    return globalThis.prompt?.(message, value) ?? null;
  }

  protected confirm(message: string): boolean {
    return globalThis.confirm?.(message) ?? true;
  }

  // ---- presentation helpers ----

  rowPad(depth: number): number {
    return 6 + depth * 16;
  }

  private currentTreeRowIndex(event: KeyboardEvent, rows: readonly WikiTreeRow[]): number {
    const target = event.target as HTMLElement | null;
    const rowEl = target?.closest?.('.pwiki__row') as HTMLElement | null;
    const domId = rowEl?.dataset['wikiRowId'];
    const id = domId || this.focusedRowId() || this.openedRel();
    const index = id ? rows.findIndex(r => nodeId(r.node) === id) : -1;
    return index >= 0 ? index : 0;
  }

  private focusRowAt(index: number): void {
    const rows = this.rows();
    const row = rows[index];
    if (!row) return;
    this.focusedRowId.set(nodeId(row.node));
    queueMicrotask(() => {
      const root = this.host.nativeElement as HTMLElement;
      root.querySelectorAll<HTMLElement>('.pwiki__row')[index]?.focus();
    });
  }

  private activateTreeRow(row: WikiTreeRow): void {
    if (row.node.type === 'folder') {
      this.toggleExpand(nodeId(row.node));
      return;
    }
    if (row.node.relPath) this.openFile(row.node.relPath, row.node.type);
  }

  private parentRowIndex(rows: readonly WikiTreeRow[], index: number): number {
    const depth = rows[index]?.depth ?? 0;
    if (depth <= 0) return -1;
    for (let i = index - 1; i >= 0; i--) {
      if (rows[i].depth === depth - 1) return i;
    }
    return -1;
  }

  private formatJson(content: string): string {
    try {
      return JSON.stringify(JSON.parse(content), null, 2);
    } catch {
      return content;
    }
  }

  /** Locale date-time for the doc-header last-modified line; blank on bad input. */
  formatTimestamp(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
  }
}

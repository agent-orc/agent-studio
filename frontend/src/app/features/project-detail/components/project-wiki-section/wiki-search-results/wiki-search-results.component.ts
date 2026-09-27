import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import {
  SegmentedControlComponent,
  SegmentedOption,
} from '../../../../../components/segmented-control/segmented-control.component';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiSearchResponse, WikiSearchResult } from '../../../../../models/project-docs.model';
import { WikiStarsService } from '../wiki-stars.service';

/** How the hit list renders: grouped by folder hierarchy (default) or flat. */
export type WikiSearchViewMode = 'tree' | 'list';

/**
 * localStorage key for the view toggle, named in the style of the section's
 * `atp.projectWiki.v1.*` persistence. Deliberately project-independent: the
 * tree-vs-list preference is a reading habit, not per-project state.
 */
const VIEW_MODE_STORAGE_KEY = 'atp.projectWikiSearchView.v1';

/**
 * Synthetic group path for root-level hits (relPath without a folder). Only
 * materialises when at least one real folder group exists — root hits then
 * gather under a "(Wurzel)" group instead of floating beside the folders.
 * With exclusively root-level hits the tree stays flat (no pseudo group).
 */
export const WIKI_SEARCH_ROOT_GROUP_PATH = '(root)';

/** Display label of the synthetic root group. */
export const WIKI_SEARCH_ROOT_GROUP_LABEL = '(Wurzel)';

/** Folder node of the tree view; `label` may compress a single-child chain. */
export interface WikiSearchTreeGroup {
  kind: 'group';
  /** Display label, e.g. `wiki/concepts` when the chain was compressed. */
  label: string;
  /** Full folder path — the stable id used for expand/collapse. */
  path: string;
  /** Number of hits anywhere below this folder (badge). */
  count: number;
  /** Smallest display-order index in the subtree. */
  bestIndex: number;
  children: WikiSearchTreeNode[];
}

export interface WikiSearchTreeHit {
  kind: 'hit';
  result: WikiSearchResult;
  /** Wiki tree position, falling back to the original score order. */
  index: number;
}

export type WikiSearchTreeNode = WikiSearchTreeGroup | WikiSearchTreeHit;

/** Flattened render row of the tree view; `depth` drives the indentation. */
export type WikiSearchRow =
  | { kind: 'group'; label: string; path: string; count: number; depth: number; expanded: boolean }
  | { kind: 'hit'; result: WikiSearchResult; depth: number };

/**
 * Wiki search result list for the content pane. Purely presentational: the
 * parent owns the query state, debounce, and HTTP; this component renders the
 * hits (title, dimmed relPath, `<em>`-highlighted snippet, relative time) and
 * emits navigation / semantic-expansion intent.
 *
 * Two renderings share the identical hit row: a folder tree (default) that
 * groups hits by their relPath hierarchy — single-child folder chains are
 * compressed ("wiki/concepts"), groups and hits follow the persisted wiki tree,
 * folders toggle open/closed, plus the flat score-ordered list. The choice persists in
 * localStorage under {@link VIEW_MODE_STORAGE_KEY}.
 *
 * Snippets are bound via `[innerHTML]`; that is safe here only because
 * `ProjectDocsService.searchWiki` sanitises every snippet down to `<em>`-only
 * markup before it reaches this component (see `sanitizeWikiSearchSnippet`),
 * and Angular's default innerHTML sanitisation still applies on top.
 */
@Component({
  selector: 'app-wiki-search-results',
  standalone: true,
  imports: [NgTemplateOutlet, SegmentedControlComponent, StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-search-results.component.html',
  styleUrl: './wiki-search-results.component.scss',
})
export class WikiSearchResultsComponent {
  /** Project scope for the per-hit star toggle (empty in pure previews). */
  readonly projectName = input('');
  readonly query = input.required<string>();
  readonly response = input<WikiSearchResponse | null>(null);
  /** Persisted tree order used only by the grouped tree rendering. */
  readonly documentOrder = input<readonly string[]>([]);
  readonly loading = input(false);
  /** True while the semantic-expansion call is in flight. */
  readonly semanticLoading = input(false);
  /** True once the user asked for semantic expansion (drives the fallback hint). */
  readonly semanticRequested = input(false);
  readonly error = input<string | null>(null);

  readonly openResult = output<WikiSearchResult>();
  readonly expandSemantic = output<void>();

  private readonly stars = inject(WikiStarsService);

  readonly results = computed(() => this.response()?.results ?? []);
  readonly expandedTerms = computed(() => this.response()?.expandedTerms ?? []);

  readonly viewOptions: readonly SegmentedOption<WikiSearchViewMode>[] = [
    { value: 'tree', label: 'Baum', icon: 'folder', testid: 'wiki-search-view-tree' },
    { value: 'list', label: 'Liste', icon: 'list', testid: 'wiki-search-view-list' },
  ];

  readonly viewMode = signal<WikiSearchViewMode>(readStoredViewMode());

  /** Collapsed group paths. A new response resets to "all expanded". */
  private readonly collapsedGroups = linkedSignal<WikiSearchResponse | null, ReadonlySet<string>>({
    source: this.response,
    computation: () => new Set<string>(),
  });

  readonly treeRows = computed<WikiSearchRow[]>(() =>
    flattenWikiSearchTree(
      buildWikiSearchTree(this.results(), this.documentOrder()),
      this.collapsedGroups()));

  /** Semantic expansion was requested but the backend could not provide it. */
  readonly semanticUnavailable = computed(() => {
    const response = this.response();
    return this.semanticRequested()
      && !this.semanticLoading()
      && response !== null
      && response.semanticUsed === false;
  });

  setViewMode(mode: WikiSearchViewMode): void {
    this.viewMode.set(mode);
    try {
      globalThis.localStorage?.setItem(VIEW_MODE_STORAGE_KEY, mode);
    } catch {
      /* persistence is a convenience; toggling keeps working without storage */
    }
  }

  toggleGroup(path: string): void {
    const next = new Set(this.collapsedGroups());
    if (next.has(path)) next.delete(path);
    else next.add(path);
    this.collapsedGroups.set(next);
  }

  rowId(row: WikiSearchRow): string {
    return row.kind === 'group' ? `group:${row.path}` : `hit:${row.result.relPath}`;
  }

  /** Path tooltip of a group; the synthetic root group shows the wiki root. */
  groupTooltip(path: string): string {
    return path === WIKI_SEARCH_ROOT_GROUP_PATH ? 'docs/' : path;
  }

  /** Star state of a hit (reactive: the template read tracks the store signal). */
  isStarred(result: WikiSearchResult): boolean {
    return this.stars.isStarred(this.projectName(), result.relPath);
  }

  /** Star toggle on a hit; stops propagation so it never counts as openResult. */
  toggleStar(event: Event, result: WikiSearchResult): void {
    event.stopPropagation();
    this.stars.toggle(this.projectName(), result.relPath, result.title);
  }

  /** Compact relative time ("3h ago"), falling back to a locale date. */
  relativeTime(iso: string | null): string {
    if (!iso) return '';
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
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString('en-US');
  }
}

function readStoredViewMode(): WikiSearchViewMode {
  try {
    return globalThis.localStorage?.getItem(VIEW_MODE_STORAGE_KEY) === 'list' ? 'list' : 'tree';
  } catch {
    return 'tree';
  }
}

/** Mutable folder while the hierarchy is being assembled from relPaths. */
interface DraftFolder {
  name: string;
  path: string;
  folders: Map<string, DraftFolder>;
  hits: WikiSearchTreeHit[];
}

/**
 * Groups results into their relPath folder hierarchy. Folder
 * chains with a single folder child and no direct hits are compressed into one
 * node ("wiki/concepts"). Every level follows the saved wiki tree position;
 * hits missing from that tree follow behind in their original score order.
 *
 * Root-level hits (relPath without a folder): as soon as at least one folder
 * group exists they gather under a synthetic "(Wurzel)" group — otherwise they
 * would float beside the folders and the tree would read like the flat list.
 * With only root-level hits the tree stays flat (a lone pseudo group would be
 * noise, not structure).
 */
export function buildWikiSearchTree(
  results: readonly WikiSearchResult[],
  documentOrder: readonly string[] = [],
): WikiSearchTreeNode[] {
  const root: DraftFolder = { name: '', path: '', folders: new Map(), hits: [] };
  const savedIndex = new Map(documentOrder.map((relPath, index) => [relPath, index]));
  results.forEach((result, index) => {
    const segments = result.relPath.split('/').filter(s => s.length > 0);
    let node = root;
    for (const segment of segments.slice(0, -1)) {
      let child = node.folders.get(segment);
      if (!child) {
        child = {
          name: segment,
          path: node.path ? `${node.path}/${segment}` : segment,
          folders: new Map(),
          hits: [],
        };
        node.folders.set(segment, child);
      }
      node = child;
    }
    node.hits.push({
      kind: 'hit',
      result,
      index: savedIndex.get(result.relPath) ?? documentOrder.length + index,
    });
  });
  return groupRootHits(finishChildren(root));
}

/** Wraps top-level hits into the "(Wurzel)" group when folder groups exist. */
function groupRootHits(nodes: WikiSearchTreeNode[]): WikiSearchTreeNode[] {
  const rootHits = nodes.filter(n => n.kind === 'hit');
  if (rootHits.length === 0 || rootHits.length === nodes.length) return nodes;
  const rootGroup: WikiSearchTreeGroup = {
    kind: 'group',
    label: WIKI_SEARCH_ROOT_GROUP_LABEL,
    path: WIKI_SEARCH_ROOT_GROUP_PATH,
    count: rootHits.length,
    bestIndex: rootHits.reduce((best, h) => Math.min(best, orderIndex(h)), Number.MAX_SAFE_INTEGER),
    children: rootHits,
  };
  return [...nodes.filter(n => n.kind === 'group'), rootGroup]
    .sort((a, b) => orderIndex(a) - orderIndex(b));
}

function finishChildren(folder: DraftFolder): WikiSearchTreeNode[] {
  const nodes: WikiSearchTreeNode[] = [
    ...[...folder.folders.values()].map(finishFolder),
    ...folder.hits,
  ];
  return nodes.sort((a, b) => orderIndex(a) - orderIndex(b));
}

function finishFolder(folder: DraftFolder): WikiSearchTreeGroup {
  let label = folder.name;
  let current = folder;
  while (current.hits.length === 0 && current.folders.size === 1) {
    const only = [...current.folders.values()][0];
    label = `${label}/${only.name}`;
    current = only;
  }
  const children = finishChildren(current);
  const count = children.reduce((sum, c) => sum + (c.kind === 'hit' ? 1 : c.count), 0);
  const bestIndex = children.reduce((best, c) => Math.min(best, orderIndex(c)), Number.MAX_SAFE_INTEGER);
  return { kind: 'group', label, path: current.path, count, bestIndex, children };
}

function orderIndex(node: WikiSearchTreeNode): number {
  return node.kind === 'hit' ? node.index : node.bestIndex;
}

/** Depth-first render rows; collapsed groups keep their subtree off-DOM. */
export function flattenWikiSearchTree(
  nodes: readonly WikiSearchTreeNode[],
  collapsed: ReadonlySet<string>,
): WikiSearchRow[] {
  const rows: WikiSearchRow[] = [];
  const walk = (list: readonly WikiSearchTreeNode[], depth: number): void => {
    for (const node of list) {
      if (node.kind === 'hit') {
        rows.push({ kind: 'hit', result: node.result, depth });
        continue;
      }
      const expanded = !collapsed.has(node.path);
      rows.push({
        kind: 'group',
        label: node.label,
        path: node.path,
        count: node.count,
        depth,
        expanded,
      });
      if (expanded) walk(node.children, depth + 1);
    }
  };
  walk(nodes, 0);
  return rows;
}

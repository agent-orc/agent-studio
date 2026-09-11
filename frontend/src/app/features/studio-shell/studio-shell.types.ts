/**
 * Studio-Shell tab + activity-panel taxonomy (Agent Software Studio redesign).
 *
 * The new shell is a VS-Code-inspired editor surface: a left ActivityBar
 * swaps the Sidebar panel, the main pane shows tabs that map to one of
 * `StudioTabKind`, and a right Chat panel is collapsible. Each tab type
 * carries the minimum identifiers it needs; the renderer maps the kind
 * to an existing feature component (board, job-detail, etc.).
 */

/** Discrete tab kinds the editor area can host. */
export type StudioTabKind = 'board' | 'feed' | 'chat-history' | 'epics' | 'epic' | 'task' | 'hub' | 'workbenches' | 'workbench' | 'diff' | 'activity' | 'url-preview' | 'workspace-settings' | 'welcome';

/** Sidebar panel kinds reachable from the ActivityBar. */
export type StudioPanelKind = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook' | 'settings';

/** Board tab - one per project; key `board:<projectName>` or `board:__all__`. */
export interface BoardTab { kind: 'board'; projectName: string; }

/** Workspace-wide orchestrator Feed; singleton key `feed`. */
export interface FeedTab { kind: 'feed'; }

/** Workspace-wide Task Server Chat History; singleton key `chat-history`. */
export interface ChatHistoryTab { kind: 'chat-history'; }

/** Epic overview tab - project-scoped or workspace-wide; key `epics:<projectName|__all__>`. */
export interface EpicsTab { kind: 'epics'; projectName: string | null; }

/** Epic detail tab - one per opened epic; key `epic:<epicKey>`. */
export interface EpicTab { kind: 'epic'; epicKey: string; viewTaskKey?: string; }

/** Task-detail tab — one per opened job; key `task:<taskKey>`. */
export interface TaskTab { kind: 'task'; taskKey: string; }

/**
 * Stable target inside a project's Wiki. The document or folder path belongs
 * to the shell-tab identity. Viewer modes such as Doc, Report, Source, and Edit
 * remain local substate of that tab.
 */
export type WikiTabTarget =
  | { kind: 'overview' }
  | { kind: 'page'; relPath: string }
  | { kind: 'folder'; relPath: string };

/**
 * Deck tab. Project rails share `hub:<projectName>` and adopt a newly requested
 * section in place. Wiki targets are first-class internal destinations, keyed
 * by project plus exact page or folder path.
 */
export interface HubTab {
  kind: 'hub';
  projectName: string;
  section?: string;
  wikiTarget?: WikiTabTarget;
  /** Optional exact row target when a task post-step links into Project Pipeline. */
  pipelineStepId?: string;
}

/** Shared overview, workspace-wide or filtered to one project. */
export interface WorkbenchesTab { kind: 'workbenches'; projectName: string | null; }

/** Isolated read-only Dossier viewer, one tab per project + Dossier id. */
export interface WorkbenchTab {
  kind: 'workbench';
  projectName: string;
  /** Immutable registry id used by the canonical Project Hub URL. */
  projectId?: string;
  workbenchId: string;
  title?: string;
  /** Stable short reference shown by compact context surfaces when available. */
  key?: string;
}

/** Full-screen diff tab; key `diff:<commitSha>`. */
export interface DiffTab { kind: 'diff'; commitSha: string; }

/** Full-screen activity tab; key `activity:<taskKey>`. */
export interface ActivityTab { kind: 'activity'; taskKey: string; }

/**
 * Embedded Project-URL preview tab (AGT-2067) — one per configured URL;
 * key `url-preview:<projectName>:<urlId>`. Renders the URL inside a
 * sandboxed iframe (split-view beside the Orchestrator side sheet) instead
 * of jumping to an external browser tab.
 */
export interface UrlPreviewTab { kind: 'url-preview'; projectName: string; urlId: string; }

/** Workspace settings tab — global settings surface opened from the ActivityBar gear. */
export interface WorkspaceSettingsTab { kind: 'workspace-settings'; }

/** Welcome screen — no real tab, no key. */
export interface WelcomeTab { kind: 'welcome'; }

export type StudioTab = BoardTab | FeedTab | ChatHistoryTab | EpicsTab | EpicTab | TaskTab | HubTab | WorkbenchesTab | WorkbenchTab | DiffTab | ActivityTab | UrlPreviewTab | WorkspaceSettingsTab | WelcomeTab;

/**
 * Build the stable target identity for a tab. Replaceable substate is omitted:
 * task pane tabs, Deck rails, pipeline-row focus, and Epic child focus all
 * update the existing destination instead of creating duplicate shell tabs.
 */
export function studioTabKey(tab: StudioTab): string {
  switch (tab.kind) {
    case 'board':    return `board:${tab.projectName}`;
    case 'feed':     return 'feed';
    case 'chat-history': return 'chat-history';
    case 'epics':    return `epics:${tab.projectName ?? '__all__'}`;
    case 'epic':     return `epic:${tab.epicKey}`;
    case 'task':     return `task:${tab.taskKey}`;
    case 'hub': {
      if (tab.section !== 'wiki') return `hub:${tab.projectName}`;
      const target = tab.wikiTarget;
      if (!target || target.kind === 'overview') return `hub:${tab.projectName}:wiki`;
      const path = target.relPath.trim().replace(/^docs\//i, '');
      return `hub:${tab.projectName}:wiki:${target.kind}:${encodeURIComponent(path)}`;
    }
    case 'workbenches': return `workbenches:${tab.projectName ?? '__all__'}`;
    case 'workbench': return `workbench:${tab.projectName}:${tab.workbenchId}`;
    case 'diff':     return `diff:${tab.commitSha}`;
    case 'activity': return `activity:${tab.taskKey}`;
    case 'url-preview': return `url-preview:${tab.projectName}:${tab.urlId}`;
    case 'workspace-settings': return 'workspace-settings';
    case 'welcome':  return 'welcome';
  }
}

import { Injectable, computed, signal } from '@angular/core';
import { routeSegmentOf, withRouteSegment } from '../../../services/url-hash.util';

/**
 * Sections of the single, consolidated Workspace-settings view.
 *
 * AGT-2035 folded the formerly scattered surfaces into one view with a clean
 * Global-vs-Workspace split:
 *   - Global (per-user / app-wide): `appearance` (Theme + Activity bar),
 *     `updates`, `workspaces` (registry management, moved off the sidebar),
 *     `task-server` (the durable task server's URL, store, evidence git,
 *     client registry and management sweeps — AGT-1924), `remote-hosts`,
 *     `orchestrator` (the platform-global supervisor / orchestrator lifecycle
 *     flags — AGT-1812 retired their standalone modal into this section).
 *   - Workspace defaults: `caps` (the "CLI Management" hub - CLI catalog,
 *     models/routes, usage caps and completion contracts), `cli-sessions`
 *     and `cli-paths` (the encapsulated CLI-session inventory and per-CLI
 *     filesystem-location pages split out of the CLI Management hub -
 *     AGT-2101), `working-memory` (extracted from caps), `prompts`,
 *     `tokens` (now the single usage area), `screenshots`.
 * `overview` is the landing rail item that links into each section.
 *
 * The `summary` section was removed (executive summary is a project-level
 * concern); its old deep-links now resolve to `overview` instead of crashing.
 */
export type WorkspaceSettingsSection =
  | 'overview'
  | 'appearance'
  | 'updates'
  | 'workspaces'
  | 'task-server'
  | 'remote-hosts'
  | 'orchestrator'
  | 'caps'
  | 'cli-sessions'
  | 'cli-paths'
  | 'working-memory'
  | 'prompts'
  | 'retention'
  | 'tokens'
  | 'screenshots';

export type WorkspaceTokenUsagePage = 'workspace' | 'claude' | 'codex';

/**
 * Shell-feature service: open/close state + URL-hash sync for the consolidated
 * Workspace-settings view. One view, one open flag, one active section.
 *
 * The view is deep-linkable so a bookmark or copy-pasted address reproduces the
 * open section. Canonical routes live below `#/workspace/settings`; legacy
 * per-overlay hashes (`#/workspace/tokens`, `#/workspace/screenshots`) remain
 * readable migration inputs. The retired `#/workspace/summary` / `#/summary`
 * aliases resolve to the overview.
 *
 * The deep link is the hash's ROUTE SEGMENT and coexists with key-value
 * segments such as the board's `filters=...` (see url-hash.util.ts):
 * reads match the route segment (never the whole hash), writes replace only
 * the route segment, and closing removes only an own route. A hash like
 * `#/workspace/settings&filters=...` therefore restores both surfaces.
 *
 * Rendering lives in `WorkspaceOverlaysComponent`; the shell just mounts that
 * component once (modal in legacy layout, inline editor tab in the studio
 * shell).
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceOverlaysService {
  /** Whether the consolidated Workspace-settings view is open. */
  readonly settingsOpen = signal<boolean>(false);
  /** The active section inside the view. */
  readonly section = signal<WorkspaceSettingsSection>('overview');
  readonly tokenUsagePage = signal<WorkspaceTokenUsagePage>('workspace');

  /**
   * Back-compat read signals. Each loose overlay is now a section of the one
   * view; the shell and existing template guards keep reading these without
   * caring that the underlying state collapsed into one surface.
   */
  readonly tokensOpen = computed(() => this.settingsOpen() && this.section() === 'tokens');
  readonly screenshotsOpen = computed(() => this.settingsOpen() && this.section() === 'screenshots');
  readonly cliAdminOpen = computed(() => this.settingsOpen() && this.section() === 'caps');
  readonly promptAdminOpen = computed(() => this.settingsOpen() && this.section() === 'prompts');

  /** True iff the view is open in any section. */
  readonly anyOpen = computed(() => this.settingsOpen());

  /**
   * True iff the view is open on a section OTHER than 'caps'. The 'caps'
   * (CLI usage) section has its own dedicated "Usage" status-bar pill, so the
   * "Settings" pill must not also light up while Usage is showing — otherwise
   * both pills carry the single `--studio-accent` active fill at once (see
   * docs/quality/frontend/design-system.md, "one accent per rail").
   */
  readonly anyOpenExceptUsage = computed(() => this.settingsOpen() && this.section() !== 'caps');

  /**
   * True when the current visible state was reached by a deep-link hash, so a
   * back/forward navigation that drops the hash closes the view.
   */
  private openedViaHash = false;

  // ---------- generic view control ----------

  open(section: WorkspaceSettingsSection): void {
    this.section.set(section);
    if (section === 'tokens') this.tokenUsagePage.set('workspace');
    this.settingsOpen.set(true);
    this.openedViaHash = false;
    this.writeRoute(this.routeForSection(section));
  }

  /** Switch the active section, opening the view first if it is closed. */
  select(section: WorkspaceSettingsSection): void {
    if (!this.settingsOpen()) {
      this.open(section);
      return;
    }
    if (this.section() === section) return;
    this.section.set(section);
    this.writeRoute(this.routeForSection(section));
  }

  selectTokenUsagePage(page: WorkspaceTokenUsagePage): void {
    this.tokenUsagePage.set(page);
    this.writeRoute(page === 'workspace'
      ? '/workspace/settings/tokens'
      : `/workspace/settings/tokens/${page}`);
  }

  close(): void {
    if (!this.settingsOpen()) return;
    this.settingsOpen.set(false);
    this.openedViaHash = false;
    this.clearOwnHash();
  }

  toggle(section: WorkspaceSettingsSection): void {
    if (this.settingsOpen() && this.section() === section) this.close();
    else this.open(section);
  }

  // ---------- view landing ----------

  openSettings(): void { this.open('overview'); }
  toggleSettings(): void { this.toggle('overview'); }

  // ---------- back-compat wrappers ----------
  // External call sites (status bar, usage hover panel, studio shell,
  // screenshot reel) still call these; they now route into the view.

  openTokens(): void { this.open('tokens'); }
  closeTokens(): void { this.close(); }
  toggleTokens(): void { this.toggle('tokens'); }

  openScreenshots(): void { this.open('screenshots'); }
  closeScreenshots(): void { this.close(); }
  toggleScreenshots(): void { this.toggle('screenshots'); }

  openCliAdmin(): void { this.open('caps'); }
  closeCliAdmin(): void { this.close(); }
  toggleCliAdmin(): void { this.toggle('caps'); }

  openPromptAdmin(): void { this.open('prompts'); }
  closePromptAdmin(): void { this.close(); }
  togglePromptAdmin(): void { this.toggle('prompts'); }

  /**
   * AGT-1812: open the platform-global orchestrator / supervisor lifecycle
   * flags. This is the new home of the retired standalone "Orchestrator config"
   * modal — the header Dev-tools entry and the orchestrator side-sheet gear both
   * route here now.
   */
  openOrchestrator(): void { this.open('orchestrator'); }
  toggleOrchestrator(): void { this.toggle('orchestrator'); }

  /**
   * Reconcile open state with the current URL hash. Call once on app boot and
   * on every `hashchange` event. A recognised section route opens (or
   * switches) the view; dropping a route that opened the view closes it.
   * Matching happens on the hash's route segment, so key-value segments such
   * as `filters=...` neither hide the deep link nor get disturbed by it.
   */
  syncFromHash(): void {
    const route = routeSegmentOf(window.location.hash) ?? '';
    const section = this.sectionForRoute(route);
    if (section) {
      this.tokenUsagePage.set(this.tokenUsagePageForRoute(route));
      if (this.section() !== section) this.section.set(section);
      if (!this.settingsOpen()) this.settingsOpen.set(true);
      this.openedViaHash = true;
      const tokenPage = this.tokenUsagePage();
      const canonicalRoute = section === 'tokens' && tokenPage !== 'workspace'
        ? `/workspace/settings/tokens/${tokenPage}`
        : this.routeForSection(section);
      if (route !== canonicalRoute) {
        this.writeRoute(canonicalRoute);
      }
    } else if (this.settingsOpen() && this.openedViaHash) {
      this.settingsOpen.set(false);
      this.openedViaHash = false;
    }
  }

  private sectionForRoute(route: string): WorkspaceSettingsSection | null {
    switch (route) {
      case '/workspace/tokens': return 'tokens';
      case '/workspace/tokens/claude': return 'tokens';
      case '/workspace/tokens/codex': return 'tokens';
      case '/workspace/settings/tokens': return 'tokens';
      case '/workspace/settings/tokens/claude': return 'tokens';
      case '/workspace/settings/tokens/codex': return 'tokens';
      case '/workspace/screenshots': return 'screenshots';
      case '/workspace/settings/screenshots': return 'screenshots';
      case '/workspace/settings/caps':
      case '/workspace/caps': return 'caps';
      case '/workspace/settings/cli-sessions': return 'cli-sessions';
      case '/workspace/settings/cli-paths': return 'cli-paths';
      case '/workspace/settings/prompts':
      case '/workspace/prompts': return 'prompts';
      case '/workspace/settings/retention': return 'retention';
      case '/workspace/settings/appearance': return 'appearance';
      case '/workspace/settings/updates': return 'updates';
      case '/workspace/settings/workspaces': return 'workspaces';
      case '/workspace/settings/task-server': return 'task-server';
      case '/workspace/settings/execution-hosts':
      case '/workspace/settings/remote-hosts': return 'remote-hosts';
      // Retired project-source catalogue: old bookmarks land safely on Overview.
      case '/workspace/settings/project-sources': return 'overview';
      case '/workspace/settings/orchestrator': return 'orchestrator';
      case '/workspace/settings/working-memory': return 'working-memory';
      // Retired 'summary' aliases resolve to the overview (migration: no crash).
      case '/workspace/summary':
      case '/summary':
      case '/workspace/settings': return 'overview';
      default: return null;
    }
  }

  private tokenUsagePageForRoute(route: string): WorkspaceTokenUsagePage {
    if (route === '/workspace/tokens/claude' || route === '/workspace/settings/tokens/claude') {
      return 'claude';
    }
    if (route === '/workspace/tokens/codex' || route === '/workspace/settings/tokens/codex') {
      return 'codex';
    }
    return 'workspace';
  }

  private routeForSection(section: WorkspaceSettingsSection): string {
    switch (section) {
      case 'tokens': return '/workspace/settings/tokens';
      case 'screenshots': return '/workspace/settings/screenshots';
      case 'caps': return '/workspace/settings/caps';
      case 'cli-sessions': return '/workspace/settings/cli-sessions';
      case 'cli-paths': return '/workspace/settings/cli-paths';
      case 'prompts': return '/workspace/settings/prompts';
      case 'retention': return '/workspace/settings/retention';
      case 'appearance': return '/workspace/settings/appearance';
      case 'updates': return '/workspace/settings/updates';
      case 'workspaces': return '/workspace/settings/workspaces';
      case 'task-server': return '/workspace/settings/task-server';
      case 'remote-hosts': return '/workspace/settings/execution-hosts';
      case 'orchestrator': return '/workspace/settings/orchestrator';
      case 'working-memory': return '/workspace/settings/working-memory';
      case 'overview': return '/workspace/settings';
    }
  }

  private readonly ownRoutes = new Set<string>([
    '/workspace/settings',
    '/workspace/settings/caps',
    '/workspace/settings/cli-sessions',
    '/workspace/settings/cli-paths',
    '/workspace/settings/prompts',
    '/workspace/settings/retention',
    '/workspace/settings/appearance',
    '/workspace/settings/updates',
    '/workspace/settings/workspaces',
    '/workspace/settings/task-server',
    '/workspace/settings/execution-hosts',
    '/workspace/settings/remote-hosts',
    '/workspace/settings/project-sources',
    '/workspace/settings/orchestrator',
    '/workspace/settings/working-memory',
    '/workspace/settings/tokens',
    '/workspace/settings/tokens/claude',
    '/workspace/settings/tokens/codex',
    '/workspace/settings/screenshots',
    '/workspace/caps',
    '/workspace/prompts',
    '/workspace/tokens',
    '/workspace/tokens/claude',
    '/workspace/tokens/codex',
    '/workspace/screenshots',
    // Retired aliases stay here so a stale summary route still clears on close.
    '/workspace/summary',
    '/summary',
  ]);

  /** Swap the hash's route segment for `route`, keeping all other segments. */
  private writeRoute(route: string): void {
    const target = withRouteSegment(window.location.hash, route);
    if (window.location.hash !== target) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  /** Remove an own route segment, keeping foreign segments (e.g. filters). */
  private clearOwnHash(): void {
    const route = routeSegmentOf(window.location.hash);
    if (route && this.ownRoutes.has(route)) {
      const target = withRouteSegment(window.location.hash, null);
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }
}

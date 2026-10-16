import { Injectable, computed, signal } from '@angular/core';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  ProjectRailKey,
  toProjectSlug,
} from '../components/project-shell/project-shell.config';
import { routeSegmentOf, withRouteSegment } from '../../../services/url-hash.util';

/**
 * Cycle 9g project-detail feature service: open/close state + URL-hash
 * sync for the four per-project overlays that stack above the kanban
 * shell:
 *
 *   - orchestrator-feed (per-project log)          `#/project/<slug>/feed`
 *   - project-shell     (full project window)      `#/projects/<slug>[/<rail>]`
 *   - analysis-report   (drill-down on one report) no hash
 *
 * Lifted out of `app.ts` per ADR-0034. The `<app-project-overlays>`
 * container injects this service and renders all four overlays. The
 * shell keeps thin pass-through methods for the entry points that
 * remain shell-coordinated. The legacy project-detail overlay is folded
 * into the project-shell `settings` rail.
 *
 * The project-shell hash sync requires the workspace's watch-paths
 * (slug → name resolution); the shell calls `syncShellFromHash` on the
 * `hashchange` event and on `/api/watch-paths` success, passing the
 * current entries.
 */
@Injectable({ providedIn: 'root' })
export class ProjectOverlaysService {
  readonly orchFeedProject = signal<string | null>(null);
  readonly projectShellName = signal<string | null>(null);
  readonly projectShellRail = signal<ProjectRailKey>(DEFAULT_PROJECT_RAIL_KEY);
  readonly analysisReportFocus = signal<{ project: string; reportId: string } | null>(null);

  readonly anyOpen = computed(() =>
    this.orchFeedProject() !== null
    || this.projectShellName() !== null
    || this.analysisReportFocus() !== null);

  // Route segments per url-hash.util.ts: matched and written as the hash's
  // single route segment so they coexist with `filters=...` etc.
  private readonly shellRoutePrefix = '/projects/';
  // Singular `/project/` (no trailing `s`) so the feed anchor cannot be
  // confused with the plural `/projects/` project-shell prefix above.
  private readonly feedRoutePrefix = '/project/';
  private readonly feedRouteSuffix = '/feed';
  /**
   * True when the visible feed was reached via a deep-link hash, so a
   * back/forward navigation that drops the hash closes it. A feed opened
   * by a button (toolbar / hub) or stacked over the project-shell
   * (`openFeedFromShell`) survives unrelated hash churn — without this
   * flag a shell-hash reconciliation would yank the stacked feed shut.
   */
  private openedFeedViaHash = false;

  // ---------- orch-feed (URL-deep-linked) ----------

  /**
   * Open the per-project orchestrator feed and stamp a deep-link hash
   * (`#/project/<slug>/feed`) so a bookmark or reload reproduces the
   * open feed. Mirrors the project-shell anchor contract; pushState does
   * not fire `hashchange`, so callers re-run `syncFeedFromHash` on the
   * next watch-path resolution to keep the slug → name mapping honest.
   */
  openOrchFeed(name: string): void {
    this.orchFeedProject.set(name);
    this.openedFeedViaHash = false;
    const slug = toProjectSlug(name);
    if (!slug) return;
    const targetRoute = `${this.feedRoutePrefix}${slug}${this.feedRouteSuffix}`;
    if (routeSegmentOf(window.location.hash) !== targetRoute) {
      const target = withRouteSegment(window.location.hash, targetRoute);
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
    this.openedFeedViaHash = false;
    if (this.isFeedRoute(routeSegmentOf(window.location.hash))) {
      const target = withRouteSegment(window.location.hash, null);
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  /**
   * Reconcile the orch-feed signal with the URL hash. Accepts
   * `#/project/<slug>/feed`. Slug → name resolution requires the
   * workspace watch-paths; if they have not loaded yet we leave the
   * signal alone — call again when they do (the shell re-runs this on
   * `/api/watch-paths` success, same as the project-shell sync).
   */
  syncFeedFromHash(watchPaths: readonly { name: string }[]): void {
    const route = routeSegmentOf(window.location.hash);
    if (route === null || !this.isFeedRoute(route)) {
      // Only a hash-opened feed closes when its hash is dropped; a
      // button-opened or shell-stacked feed survives unrelated churn.
      if (this.openedFeedViaHash && this.orchFeedProject() !== null) {
        this.orchFeedProject.set(null);
        this.openedFeedViaHash = false;
      }
      return;
    }
    const slug = decodeURIComponent(
      route.slice(this.feedRoutePrefix.length, route.length - this.feedRouteSuffix.length)
    ).toLowerCase();
    if (!slug) return;
    if (watchPaths.length === 0) return;
    const match = watchPaths.find(wp => toProjectSlug(wp.name) === slug);
    if (!match) {
      if (this.openedFeedViaHash && this.orchFeedProject() !== null) {
        this.orchFeedProject.set(null);
        this.openedFeedViaHash = false;
      }
      return;
    }
    if (this.orchFeedProject() !== match.name) this.orchFeedProject.set(match.name);
    this.openedFeedViaHash = true;
  }

  private isFeedRoute(route: string | null): boolean {
    return route !== null
      && route.startsWith(this.feedRoutePrefix) && route.endsWith(this.feedRouteSuffix)
      && route.length > this.feedRoutePrefix.length + this.feedRouteSuffix.length;
  }

  // ---------- project-shell (URL-deep-linked) ----------

  openProjectShell(name: string, rail: ProjectRailKey = DEFAULT_PROJECT_RAIL_KEY,
                   watchPaths: readonly { name: string }[] = []): void {
    const slug = toProjectSlug(name);
    if (!slug) return;
    const targetRoute = `${this.shellRoutePrefix}${slug}`
      + (rail !== DEFAULT_PROJECT_RAIL_KEY ? `/${rail}` : '');
    if (routeSegmentOf(window.location.hash) !== targetRoute) {
      const target = withRouteSegment(window.location.hash, targetRoute);
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
    // pushState doesn't fire hashchange; apply the resolved state directly.
    this.syncShellFromHash(watchPaths);
  }

  closeProjectShell(): void {
    this.projectShellName.set(null);
    this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
    if (routeSegmentOf(window.location.hash)?.startsWith(this.shellRoutePrefix)) {
      const target = withRouteSegment(window.location.hash, null);
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  setProjectShellRail(key: ProjectRailKey): void {
    const name = this.projectShellName();
    if (!name) return;
    this.projectShellRail.set(key);
    const slug = toProjectSlug(name);
    const targetRoute = `${this.shellRoutePrefix}${slug}`
      + (key !== DEFAULT_PROJECT_RAIL_KEY ? `/${key}` : '');
    if (routeSegmentOf(window.location.hash) !== targetRoute) {
      const target = withRouteSegment(window.location.hash, targetRoute);
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  /** Keep the mounted project shell and its deep-link valid after a registry rename. */
  renameOpenProjectShell(name: string): void {
    if (!name.trim() || this.projectShellName() === null) return;
    this.projectShellName.set(name.trim());
    const slug = toProjectSlug(name);
    if (!slug) return;
    const rail = this.projectShellRail();
    // A rename only swaps the slug; the rail and any rail-owned deep-link query
    // (e.g. the Wiki's `?page=`/`?folder=`) are unaffected, so carry the current
    // route's query suffix over verbatim rather than dropping it from the URL.
    const targetRoute = `${this.shellRoutePrefix}${slug}`
      + (rail !== DEFAULT_PROJECT_RAIL_KEY ? `/${rail}` : '')
      + this.currentShellRouteQuery();
    if (routeSegmentOf(window.location.hash) !== targetRoute) {
      const target = withRouteSegment(window.location.hash, targetRoute);
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  /** The `?…` deep-link query on the current shell route, or '' when none/off-route. */
  private currentShellRouteQuery(): string {
    const route = routeSegmentOf(window.location.hash);
    if (route === null || !route.startsWith(this.shellRoutePrefix)) return '';
    const q = route.indexOf('?');
    return q >= 0 ? route.slice(q) : '';
  }

  /**
   * Cross-overlay nav: clicking "open feed" inside the project-shell
   * stacks the orchestrator feed over the shell. The shell stays
   * mounted so closing the feed returns to the same rail.
   */
  openFeedFromShell(): void {
    const name = this.projectShellName();
    if (!name) return;
    this.orchFeedProject.set(name);
  }

  /**
   * Cross-overlay nav retained for the settings rail, which mounts the
   * former project-detail component inside the central project window.
   */
  openFeedFromDetail(name: string): void {
    this.orchFeedProject.set(name);
  }

  // ---------- analysis-report ----------

  openAnalysisReport(project: string, reportId: string): void {
    this.analysisReportFocus.set({ project, reportId });
  }

  closeAnalysisReport(): void {
    this.analysisReportFocus.set(null);
  }

  /**
   * Reconcile the project-shell signals with the URL hash. Accepts
   * `#/projects/<slug>` and `#/projects/<slug>/<rail-key>`. Slug→name
   * resolution requires the workspace watch-paths; if they haven't
   * loaded yet we leave the signals alone — call again when they do.
   */
  syncShellFromHash(watchPaths: readonly { name: string }[]): void {
    const route = routeSegmentOf(window.location.hash);
    if (route === null || !route.startsWith(this.shellRoutePrefix)) {
      if (this.projectShellName() !== null) {
        this.projectShellName.set(null);
        this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
      }
      return;
    }
    // Tolerate a deep-link query suffix on the rail segment: the Wiki rail
    // carries its open page/folder as `#/projects/<slug>/wiki?page=<relPath>`
    // (see wiki-deep-link.ts). Strip everything from the first `?` before the
    // slug/rail split so the rail still resolves to `wiki` (not a bogus key).
    const rawTail = route.slice(this.shellRoutePrefix.length);
    const queryIndex = rawTail.indexOf('?');
    const tail = queryIndex >= 0 ? rawTail.slice(0, queryIndex) : rawTail;
    const [slugRaw, railRaw] = tail.split('/', 2);
    const slug = decodeURIComponent(slugRaw || '').toLowerCase();
    if (!slug) return;
    if (watchPaths.length === 0) return;
    const match = watchPaths.find(wp => toProjectSlug(wp.name) === slug);
    if (!match) {
      if (this.projectShellName() !== null) {
        this.projectShellName.set(null);
        this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
      }
      return;
    }
    const railKey: ProjectRailKey = railRaw === 'deployment' ? 'deploy' : (isProjectRailKey(railRaw) ? railRaw : DEFAULT_PROJECT_RAIL_KEY);
    if (this.projectShellName() !== match.name) this.projectShellName.set(match.name);
    if (this.projectShellRail() !== railKey) this.projectShellRail.set(railKey);
  }
}

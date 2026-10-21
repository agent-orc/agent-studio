import { beforeEach, describe, expect, it } from 'vitest';
import { ProjectOverlaysService } from './project-overlays.service';

/**
 * Deep-link anchor contract for the per-project orchestrator feed
 * (`#/project/<slug>/feed`). The feed used to open with no URL hash, so a
 * bookmark or reload could not reproduce the open feed. These tests lock:
 *   - opening stamps the singular `#/project/<slug>/feed` anchor,
 *   - closing clears it,
 *   - a fresh service reconciles the open feed from the hash on boot,
 *   - the anchor is distinct from the plural `#/projects/` shell prefix,
 *   - a shell-stacked feed (no hash) is not yanked shut by a non-feed hash.
 */
describe('ProjectOverlaysService · orch-feed deep-link anchor', () => {
  const watchPaths = [{ name: 'Agent Task Processor' }, { name: 'Runbook' }];

  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('requests deferred overlays for every open overlay and clears when each closes', () => {
    const svc = new ProjectOverlaysService();
    expect(svc.anyOpen()).toBe(false);

    svc.orchFeedProject.set('Runbook');
    expect(svc.anyOpen()).toBe(true);
    svc.orchFeedProject.set(null);
    expect(svc.anyOpen()).toBe(false);

    svc.projectShellName.set('Runbook');
    expect(svc.anyOpen()).toBe(true);
    svc.projectShellName.set(null);
    expect(svc.anyOpen()).toBe(false);

    svc.analysisReportFocus.set({ project: 'Runbook', reportId: 'report-1' });
    expect(svc.anyOpen()).toBe(true);
    svc.analysisReportFocus.set(null);
    expect(svc.anyOpen()).toBe(false);
  });

  it('stamps #/project/<slug>/feed when the feed opens', () => {
    const svc = new ProjectOverlaysService();
    svc.openOrchFeed('Agent Task Processor');
    expect(svc.orchFeedProject()).toBe('Agent Task Processor');
    expect(window.location.hash).toBe('#/project/agent-task-processor/feed');
  });

  it('uses the singular prefix so it cannot collide with the #/projects/ shell prefix', () => {
    const svc = new ProjectOverlaysService();
    svc.openOrchFeed('Runbook');
    expect(window.location.hash.startsWith('#/projects/')).toBe(false);
    expect(window.location.hash).toBe('#/project/runbook/feed');
  });

  it('clears the feed anchor when the feed closes', () => {
    const svc = new ProjectOverlaysService();
    svc.openOrchFeed('Runbook');
    svc.closeOrchFeed();
    expect(svc.orchFeedProject()).toBeNull();
    expect(window.location.hash).toBe('');
  });

  it('reproduces the open feed from the hash on boot (reload / bookmark)', () => {
    history.replaceState(null, '', '/#/project/runbook/feed');
    const fresh = new ProjectOverlaysService();
    expect(fresh.orchFeedProject()).toBeNull();
    fresh.syncFeedFromHash(watchPaths);
    expect(fresh.orchFeedProject()).toBe('Runbook');
  });

  it('leaves the feed signal alone until watch-paths resolve the slug', () => {
    history.replaceState(null, '', '/#/project/runbook/feed');
    const svc = new ProjectOverlaysService();
    svc.syncFeedFromHash([]);
    expect(svc.orchFeedProject()).toBeNull();
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBe('Runbook');
  });

  it('closes a hash-opened feed when its hash is dropped', () => {
    history.replaceState(null, '', '/#/project/runbook/feed');
    const svc = new ProjectOverlaysService();
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBe('Runbook');

    history.replaceState(null, '', window.location.pathname + window.location.search);
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBeNull();
  });

  it('does not yank a shell-stacked feed (opened without a hash) shut on a non-feed hash', () => {
    const svc = new ProjectOverlaysService();
    svc.projectShellName.set('Runbook');
    svc.openFeedFromShell();
    expect(svc.orchFeedProject()).toBe('Runbook');

    // A shell-hash reconciliation fires while the feed is stacked.
    history.replaceState(null, '', '/#/projects/runbook/overview');
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBe('Runbook');
  });

  it('resolves the wiki rail even when a deep-link query rides on the hash', () => {
    // The Wiki carries its open page as `#/projects/<slug>/wiki?page=<relPath>`
    // (wiki-deep-link.ts). The `?…` suffix must not corrupt the rail parse.
    history.replaceState(null, '', '/#/projects/runbook/wiki?page=concepts%2Foverview.md');
    const svc = new ProjectOverlaysService();
    svc.syncShellFromHash(watchPaths);
    expect(svc.projectShellName()).toBe('Runbook');
    expect(svc.projectShellRail()).toBe('wiki');
  });

  it('keeps the open settings shell and hash aligned after a project rename', () => {
    const svc = new ProjectOverlaysService();
    svc.projectShellName.set('Old Name');
    svc.projectShellRail.set('settings');
    history.replaceState(null, '', '/#/projects/old-name/settings');

    svc.renameOpenProjectShell('New Name');

    expect(svc.projectShellName()).toBe('New Name');
    expect(window.location.hash).toBe('#/projects/new-name/settings');
  });

  it('preserves an open wiki deep-link query across a project rename', () => {
    // A rename swaps only the slug; the Wiki's `?page=` deep-link belongs to the
    // (unchanged) rail and must survive so the address bar stays shareable.
    const svc = new ProjectOverlaysService();
    svc.projectShellName.set('Old Name');
    svc.projectShellRail.set('wiki');
    history.replaceState(null, '', '/#/projects/old-name/wiki?page=concepts%2Foverview.md');

    svc.renameOpenProjectShell('New Name');

    expect(svc.projectShellName()).toBe('New Name');
    expect(window.location.hash).toBe('#/projects/new-name/wiki?page=concepts%2Foverview.md');
  });

  /**
   * Coexistence with the board's `filters=` segment (url-hash.util.ts). The
   * shell / feed routes are the hash's route segment; a filters= segment
   * riding alongside must neither hide the route on read nor be dropped on
   * open/close.
   */
  describe('coexistence with a board filter segment', () => {
    const FILTERS = 'filters=type%3Abug';

    it('opening the feed keeps an existing filters segment', () => {
      history.replaceState(null, '', `/#${FILTERS}`);
      const svc = new ProjectOverlaysService();

      svc.openOrchFeed('Runbook');

      expect(window.location.hash).toBe(`#/project/runbook/feed&${FILTERS}`);
    });

    it('closing the feed removes only its route, keeping filters=', () => {
      history.replaceState(null, '', `/#/project/runbook/feed&${FILTERS}`);
      const svc = new ProjectOverlaysService();
      svc.syncFeedFromHash(watchPaths);
      expect(svc.orchFeedProject()).toBe('Runbook');

      svc.closeOrchFeed();

      expect(window.location.hash).toBe(`#${FILTERS}`);
    });

    it('resolves the project shell from a composite hash with a leading filters segment', () => {
      history.replaceState(null, '', `/#${FILTERS}&/projects/runbook/wiki`);
      const svc = new ProjectOverlaysService();

      svc.syncShellFromHash(watchPaths);

      expect(svc.projectShellName()).toBe('Runbook');
      expect(svc.projectShellRail()).toBe('wiki');
    });
  });
});

import { beforeEach, describe, expect, it } from 'vitest';
import { WorkspaceOverlaysService } from './workspace-overlays.service';

describe('WorkspaceOverlaysService retired project-sources route', () => {
  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('migrates a stale deep-link to the settings overview', () => {
    history.replaceState(null, '', '/#/workspace/settings/project-sources');
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.settingsOpen()).toBe(true);
    expect(service.section()).toBe('overview');
    expect(window.location.hash).toBe('#/workspace/settings');
  });
});

/**
 * Regression for the hybrid-hash collision (operator report 2026-07-21):
 * `#filters=...&/workspace/settings`. The settings deep link and the board's
 * `filters=` segment are independent hash segments (url-hash.util.ts) and must
 * coexist: opening/closing settings never disturbs the filter segment, and a
 * composite hash still opens the recognised section.
 */
describe('WorkspaceOverlaysService coexistence with a board filter segment', () => {
  const FILTERS = 'filters=projects%3AAgent%20Studio%20Marketing';

  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('opening a section keeps an existing filters segment', () => {
    history.replaceState(null, '', `/#${FILTERS}`);
    const service = new WorkspaceOverlaysService();

    service.open('overview');

    expect(window.location.hash).toBe(`#/workspace/settings&${FILTERS}`);
  });

  it('recognises the section from a composite hash regardless of segment order', () => {
    history.replaceState(null, '', `/#${FILTERS}&/workspace/settings/task-server`);
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.settingsOpen()).toBe(true);
    expect(service.section()).toBe('task-server');
  });

  it('closing settings removes only its own route, leaving the filters segment', () => {
    history.replaceState(null, '', `/#/workspace/settings&${FILTERS}`);
    const service = new WorkspaceOverlaysService();
    service.syncFromHash();
    expect(service.settingsOpen()).toBe(true);

    service.close();

    expect(service.settingsOpen()).toBe(false);
    expect(window.location.hash).toBe(`#${FILTERS}`);
  });
});

describe('WorkspaceOverlaysService canonical settings routes', () => {
  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('writes every settings section below one canonical path', () => {
    const service = new WorkspaceOverlaysService();

    service.open('tokens');
    expect(window.location.hash).toBe('#/workspace/settings/tokens');

    service.selectTokenUsagePage('codex');
    expect(window.location.hash).toBe('#/workspace/settings/tokens/codex');

    service.select('screenshots');
    expect(window.location.hash).toBe('#/workspace/settings/screenshots');

    service.select('remote-hosts');
    expect(window.location.hash).toBe('#/workspace/settings/environment');
  });

  it('opens the review effectiveness page from its deep link and keeps it canonical', () => {
    history.replaceState(null, '', '/#/workspace/settings/tokens/reviews');
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.section()).toBe('tokens');
    expect(service.tokenUsagePage()).toBe('reviews');
    expect(window.location.hash).toBe('#/workspace/settings/tokens/reviews');
  });

  it('reads legacy loose routes and republishes the canonical path', () => {
    history.replaceState(null, '', '/#/workspace/tokens/claude');
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.settingsOpen()).toBe(true);
    expect(service.section()).toBe('tokens');
    expect(service.tokenUsagePage()).toBe('claude');
    expect(window.location.hash).toBe('#/workspace/settings/tokens/claude');
  });

  it('migrates the former remote-hosts route to Environment', () => {
    history.replaceState(null, '', '/#/workspace/settings/remote-hosts');
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.settingsOpen()).toBe(true);
    expect(service.section()).toBe('remote-hosts');
    expect(window.location.hash).toBe('#/workspace/settings/environment');
  });

  it('redirects the former execution-hosts route to Environment', () => {
    history.replaceState(null, '', '/#/workspace/settings/execution-hosts');
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.section()).toBe('remote-hosts');
    expect(window.location.hash).toBe('#/workspace/settings/environment');
  });
});

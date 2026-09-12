import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { vi } from 'vitest';
import { of } from 'rxjs';
import { ProjectWikiSectionComponent } from './project-wiki-section';
import { WikiStarsService } from './wiki-stars.service';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { WIKI_LIVE_REFRESH_MS } from '../../services/wiki-live-refresh.service';
import { WikiLinkedElementsComponent } from './wiki-linked-elements/wiki-linked-elements.component';
import {
  ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE,
  ISOLATED_HTML_ANCHORS_READY_MESSAGE,
  ISOLATED_HTML_LINK_MESSAGE,
  ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE,
} from '../../../../services/sandboxed-html.util';
import type {
  ProjectStyleGuideCatalogue,
  WikiFileHistory,
  WikiFolderOverview,
  WikiHome,
  WikiPulse,
  WikiSearchResponse,
  WikiTree,
} from '../../../../models/project-docs.model';

const TREE: WikiTree = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  root: [
    {
      name: 'concepts', title: 'concepts', relPath: 'concepts', type: 'folder', children: [
        {
          name: 'overview.md',
          title: 'Concept overview',
          relPath: 'concepts/overview.md',
          type: 'md',
          children: [],
          metadata: {
            documentMode: 'documentation',
            temporalState: 'present',
            implementationState: 'implemented',
            driftGrade: 'B',
            hasDrift: true,
            driftScore: 0.24,
            quality: 'medium',
            duplicateSuspected: false,
            duplicateGroupSize: 1,
            reportPath: 'concepts/overview.md.report.html',
            summary: 'Light sample drift.',
            companionPath: 'concepts/overview.md.meta.json',
            sourceChangedSinceReview: false,
            findingsCount: 2,
            agentReads: {
              total: 23,
              lastReadAt: '2026-07-22T10:15:00Z',
              recent: [
                { at: '2026-07-22T10:15:00Z', taskKey: 'AGT-2242' },
                { at: '2026-07-21T09:00:00Z', taskKey: 'AGT-2200' },
              ],
            },
          },
          classification: {
            status: 'ueberholt',
            supersededBy: 'concepts/new-overview.md',
            type: 'konzept',
            analyzedAt: '2026-07-18',
          },
        },
        { name: 'page.html', title: 'HTML page', relPath: 'concepts/page.html', type: 'html', children: [] },
        { name: 'page.metadata.json', title: 'Page metadata', relPath: 'concepts/page.metadata.json', type: 'json', children: [] },
      ],
    },
    { name: 'README.md', title: 'Docs index', relPath: 'README.md', type: 'md', children: [] },
  ],
};

const openTaskKey = vi.fn(() => true);
const taskNavigationStub = {
  markdownReferences: () => [
    { label: 'AGT-2050', taskKey: 'PROJ-001::agt-2050' },
    { label: 'QS-17', taskKey: 'PROJ-001::qs-17' },
  ],
  openTaskKey,
};

/** Three top-level categories (beta with a nested folder) for reorder tests. */
const REORDER_TREE: WikiTree = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  root: [
    {
      name: 'alpha', title: 'alpha', relPath: 'alpha', type: 'folder', children: [
        { name: 'a.md', title: 'Alpha page', relPath: 'alpha/a.md', type: 'md', children: [] },
      ],
    },
    {
      name: 'beta', title: 'beta', relPath: 'beta', type: 'folder', children: [
        {
          name: 'inner', title: 'inner', relPath: 'beta/inner', type: 'folder', children: [
            { name: 'i.md', title: 'Inner page', relPath: 'beta/inner/i.md', type: 'md', children: [] },
          ],
        },
        { name: 'b.md', title: 'Beta page', relPath: 'beta/b.md', type: 'md', children: [] },
      ],
    },
    {
      name: 'gamma', title: 'gamma', relPath: 'gamma', type: 'folder', children: [
        { name: 'g.md', title: 'Gamma page', relPath: 'gamma/g.md', type: 'md', children: [] },
      ],
    },
  ],
};

const PULSE: WikiPulse = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  generatedAtUtc: '2026-07-10T09:00:00Z',
  feed: {
    available: true,
    reason: null,
    items: [
      {
        relPath: 'concepts/overview.md',
        title: 'Concept overview',
        author: 'Alice',
        authorDateUtc: '2026-07-10T08:00:00Z',
        sha: 'abc1234',
        shortSha: 'abc1234',
        subject: 'AGT-2014 refine overview',
        areaSlug: 'concepts',
        areaTitle: 'concepts',
        taskKey: 'AGT-2014',
      },
    ],
  },
  inbox: {
    available: true,
    reason: null,
    count: 1,
    items: [
      {
        relPath: 'stray.md',
        title: 'Stray note',
        type: 'md',
        reason: 'Loose page at the wiki root - not filed under a category.',
      },
    ],
  },
  drift: {
    available: true,
    reason: null,
    overallGrade: 'Aging',
    areas: [
      { slug: 'concepts', title: 'concepts', grade: 'Aging', pageCount: 1, gradedPageCount: 1, worstCommitCount: 12, freshCount: 0, agingCount: 1, staleCount: 0 },
      { slug: 'operations', title: 'operations', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
    ],
    counts: { fresh: 0, aging: 1, stale: 0, graded: 1 },
  },
  critical: { available: true, reason: 'No pages have been graded yet.', count: 0, overallGrade: 'none', items: [] },
};

const STYLE_GUIDES: ProjectStyleGuideCatalogue = {
  projectKey: 'PROJ-0042',
  projectDisplayName: 'Demo',
  technologies: [
    { key: 'angular', displayLabel: 'Angular' },
    { key: 'dotnet', displayLabel: '.NET' },
  ],
  guides: [
    {
      id: 'angular-components',
      title: 'Angular component guide',
      relPath: 'quality/angular-components.md',
      summary: 'Rendering, identity, and token rules for Angular UI work.',
      promptSummary: 'Use OnPush and stable tracking.',
      version: '1',
      appliesTo: { projects: ['*'], technologies: ['angular'], taskAreas: ['frontend'] },
      match: {
        projectWildcard: true,
        projectSelector: '*',
        technologyWildcard: false,
        technologies: [{ key: 'angular', displayLabel: 'Angular' }],
      },
    },
    {
      id: 'dotnet-backend',
      title: '.NET backend guide',
      relPath: 'quality/dotnet-backend.md',
      summary: 'Feature ownership and pure policy rules for backend work.',
      promptSummary: 'Use pure policy tests.',
      version: '1',
      appliesTo: { projects: ['*'], technologies: ['dotnet'], taskAreas: ['backend'] },
      match: {
        projectWildcard: true,
        projectSelector: '*',
        technologyWildcard: false,
        technologies: [{ key: 'dotnet', displayLabel: '.NET' }],
      },
    },
  ],
  warnings: [],
  snapshotId: '0123456789abcdef',
  capturedAtUtc: '2026-07-14T08:00:00Z',
  refreshAfterUtc: '2026-07-14T08:05:00Z',
};

const HOME: WikiHome = {
  sections: [
    {
      title: 'Start',
      links: [
        { relPath: 'concepts/overview.md', label: 'Konzept-Überblick', note: 'Der Einstieg', exists: true },
        { relPath: 'workbench/overview.html', label: 'Dossier', note: null, exists: true },
        { relPath: 'missing/gone.md', label: 'Verschollen', note: 'alte Seite', exists: false },
      ],
    },
    {
      title: 'Betrieb',
      links: [{ relPath: 'ops/runbook.md', label: 'Runbook', note: null, exists: true }],
    },
  ],
};

async function setup(
  tree: WikiTree = TREE,
  pulse: WikiPulse = PULSE,
  projectId: string | null = null,
) {
  await TestBed.configureTestingModule({
    imports: [ProjectWikiSectionComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: TaskReferenceNavigationService, useValue: taskNavigationStub },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ProjectWikiSectionComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.componentRef.setInput('projectName', 'Demo');
  if (projectId) fixture.componentRef.setInput('projectId', projectId);
  fixture.detectChanges();

  http.expectOne('/api/projects/Demo/wiki/tree').flush(tree);
  flushWikiPulse(http, pulse);
  flushGradingContext(http);
  fixture.detectChanges();
  flushStyleGuidesIfRendered(http);
  flushWikiHomeIfRendered(http);
  fixture.detectChanges();
  return { fixture, http };
}

/**
 * The grading trigger seeds its maintenance-model default and the current run
 * status once per project on open (AGT-2051). Both fire from the same effect as
 * the tree/pulse load, so the fake backend must answer them or verify() trips.
 */
function flushGradingContext(http: HttpTestingController): void {
  http.expectOne('/api/cli/maintenance-model')
    .flush({ cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null });
  http.expectOne(r => r.url.includes('/wiki/grading/status')).flush({ status: null });
}

/**
 * refresh() fetches the git-backed Pulse landing view alongside the tree. Every
 * refresh (initial load and post-mutation re-read) issues it, so the fake
 * backend must answer it or verify() trips on the dangling request.
 */
function flushWikiPulse(http: HttpTestingController, pulse: WikiPulse = PULSE): void {
  http.expectOne(r => r.url.includes('/wiki/pulse')).flush(pulse);
}

/** The style-guide panel is absent when persisted state opens a document directly. */
function flushStyleGuidesIfRendered(http: HttpTestingController): void {
  const requests = http.match('/api/projects/Demo/style-guides');
  expect(requests.length).toBeLessThanOrEqual(1);
  requests[0]?.flush(STYLE_GUIDES);
}

/**
 * The curated "Einstiege" block self-fetches /wiki/home whenever the landing
 * view mounts; like the style-guide panel it is absent when persisted state
 * opens a document directly, so the request is flushed only when present.
 */
function flushWikiHomeIfRendered(http: HttpTestingController, home: WikiHome = HOME): void {
  const requests = http.match('/api/projects/Demo/wiki/home');
  expect(requests.length).toBeLessThanOrEqual(1);
  requests[0]?.flush(home);
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

function expandConcepts(fixture: { componentInstance: ProjectWikiSectionComponent; detectChanges: () => void }): void {
  fixture.componentInstance.toggleExpand('concepts');
  fixture.detectChanges();
}

function wikiStorageKey(projectName = 'Demo'): string {
  return `atp.projectWiki.v1.${encodeURIComponent(projectName)}`;
}

function metaPanelStorage(): {
  collapsed?: boolean;
  sections?: Record<string, boolean>;
} {
  return JSON.parse(localStorage.getItem('atp.wikiMetaPanel.v1') ?? '{}');
}

function clearWikiStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectWiki.v1.')
      || key.startsWith('atp.projectWikiStars.v1.')
      || key === 'atp.wikiMetaPanel.v1') {
      localStorage.removeItem(key);
    }
  }
}

/** Minimal DataTransfer backed by a Map so getData/setData round-trip in jsdom. */
function makeDataTransfer(): DataTransfer {
  const store = new Map<string, string>();
  return {
    dropEffect: 'none',
    effectAllowed: 'all',
    setData(type: string, val: string) { store.set(type, val); },
    getData(type: string) { return store.get(type) ?? ''; },
  } as unknown as DataTransfer;
}

/** Dispatches a drag event carrying a shared DataTransfer (jsdom has no DragEvent). */
function fireDrag(target: Element, type: string, dt: DataTransfer): void {
  const ev = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(ev, 'dataTransfer', { value: dt, configurable: true });
  target.dispatchEvent(ev);
}

function fakePointer(clientX: number, pointerId = 1): PointerEvent {
  const noop = () => undefined;
  return {
    clientX,
    pointerId,
    preventDefault: noop,
    currentTarget: {
      setPointerCapture: noop,
      releasePointerCapture: noop,
    },
  } as unknown as PointerEvent;
}

const HISTORY: WikiFileHistory = {
  relPath: 'concepts/overview.md',
  model: 'Claude Opus 4.8',
  metadata: {
    model: 'Claude Opus 4.8', updatedAt: '2026-06-02T00:00:00Z', reason: 'distilled',
    taskKey: null, status: null, runCount: null, hasFrontmatter: true,
  },
  commits: [
    { sha: 'abc', shortSha: 'abc1234', authorDateUtc: '2026-06-02T00:00:00Z', author: 'bot', subject: 'update', filesChanged: 1, added: 3, removed: 1 },
  ],
};

describe('ProjectWikiSectionComponent', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  beforeEach(() => {
    clearWikiStorage();
    openTaskKey.mockClear();
    // WikiHomeLinksComponent has its own focused HTTP contract tests. Keep this
    // parent suite on a stable landing-page fixture across child remounts.
    vi.spyOn(ProjectDocsService.prototype, 'getWikiHome').mockReturnValue(of(HOME));
    // Deep-link tests drive the hash; reset it so each test starts clean and
    // the existing (hash-agnostic) tests never inherit a stale wiki route.
    window.history.replaceState(null, '', '/');
  });

  it('requests a distinct Studio tab for a user-opened Wiki page', async () => {
    const { fixture, http } = await setup();
    const targets: unknown[] = [];
    fixture.componentRef.setInput('studioTabTarget', { kind: 'overview' });
    fixture.componentRef.setInput('openInternalTargetsInTabs', true);
    fixture.componentInstance.openWikiTarget.subscribe(target => targets.push(target));
    fixture.detectChanges();
    expandConcepts(fixture);

    el(fixture).querySelector<HTMLElement>(
      '[data-testid="project-wiki-file-concepts/overview.md"]',
    )?.click();
    const file = el(fixture).querySelector<HTMLElement>(
      '[data-testid="project-wiki-file-concepts/overview.md"]',
    )!;
    file.dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));
    file.dispatchEvent(new MouseEvent('auxclick', { bubbles: true, button: 1 }));
    file.closest<HTMLElement>('[role="treeitem"]')?.dispatchEvent(new MouseEvent('contextmenu', {
      bubbles: true, cancelable: true, clientX: 20, clientY: 20,
    }));
    fixture.detectChanges();
    document.querySelector<HTMLButtonElement>('[data-testid="wiki-ctx-item-open-new-tab"]')?.click();
    fixture.detectChanges();

    expect(targets).toEqual([
      { target: { kind: 'page', relPath: 'concepts/overview.md' }, reuse: 'replace-current' },
      { target: { kind: 'page', relPath: 'concepts/overview.md' }, reuse: 'new' },
      { target: { kind: 'page', relPath: 'concepts/overview.md' }, reuse: 'new' },
      { target: { kind: 'page', relPath: 'concepts/overview.md' }, reuse: 'new' },
    ]);
    expect(fixture.componentInstance.openedRel()).toBeNull();
    http.verify();
  });

  it('shows branch and commit and disables writes for a non-checkout source', async () => {
    const readonlyTree: WikiTree = {
      ...TREE,
      source: {
        mode: 'branch', branch: 'origin/develop', commit: 'abcdef1234567890',
        shortCommit: 'abcdef12', writable: false, error: null,
      },
    };
    const { fixture, http } = await setup(readonlyTree);

    expect(el(fixture).querySelector('[data-testid="project-wiki-source"]')?.textContent).toContain('origin/develop @ abcdef12');
    expect((el(fixture).querySelector('[data-testid="project-wiki-new-page"]') as HTMLButtonElement).disabled).toBe(true);
    expect((el(fixture).querySelector('[data-testid="project-wiki-new-folder"]') as HTMLButtonElement).disabled).toBe(true);

    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')?.click();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md').flush({ relPath: 'concepts/overview.md', content: '# Read only' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect((el(fixture).querySelector('[data-testid="project-wiki-edit"]') as HTMLButtonElement).disabled).toBe(true);
    http.verify();
  });

  it('shows the backend source reason when a listed page cannot be loaded', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>(
      '[data-testid="project-wiki-file-concepts/overview.md"]',
    )?.click();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md').flush({
      error: "Page 'concepts/overview.md' is not available in Wiki source 'origin/develop'.",
    }, { status: 404, statusText: 'Not Found' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush({
      error: 'File not found or path rejected',
    }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    const error = el(fixture).querySelector('[data-testid="project-wiki-load-error"]');
    expect(error?.getAttribute('role')).toBe('alert');
    expect(error?.textContent).toContain(
      "Page 'concepts/overview.md' is not available in Wiki source 'origin/develop'.",
    );
    http.verify();
  });

  it('shows the update banner on an ETag change and reloads only after confirmation', async () => {
    vi.useFakeTimers();
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')?.click();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Original' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md')
      .flush(HISTORY, { headers: { ETag: '"page-v1"' } });
    fixture.detectChanges();

    await vi.advanceTimersByTimeAsync(WIKI_LIVE_REFRESH_MS);
    const unchanged = http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md');
    expect(unchanged.request.headers.get('If-None-Match')).toBe('"page-v1"');
    unchanged.flush(null, {
      status: 304,
      statusText: 'Not Modified',
      headers: { ETag: '"page-v1"' },
    });
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-update-banner"]')).toBeNull();
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer"]')?.textContent).toContain('Original');

    await vi.advanceTimersByTimeAsync(WIKI_LIVE_REFRESH_MS);
    const changed = http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md');
    expect(changed.request.headers.get('If-None-Match')).toBe('"page-v1"');
    changed.flush({
      ...HISTORY,
      commits: [{
        ...HISTORY.commits[0],
        sha: 'def',
        shortSha: 'def5678',
        subject: 'external update',
      }],
    }, { headers: { ETag: '"page-v2"' } });
    fixture.detectChanges();

    const banner = el(fixture).querySelector('[data-testid="project-wiki-update-banner"]');
    expect(banner?.textContent).toContain('Diese Seite wurde aktualisiert.');
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer"]')?.textContent).toContain('Original');

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-update-reload"]')!.click();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Aktualisiert' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md')
      .flush({ ...HISTORY, commits: [{ ...HISTORY.commits[0], sha: 'def' }] }, {
        headers: { ETag: '"page-v2"' },
      });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-update-banner"]')).toBeNull();
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer"]')?.textContent).toContain('Aktualisiert');
    fixture.destroy();
    vi.useRealTimers();
  });

  it('renders the physical folder tree with folders collapsed by default and md/html/json files when expanded', async () => {
    const { fixture, http } = await setup();
    let text = el(fixture).querySelector('[data-testid="project-wiki-tree"]')?.textContent ?? '';
    expect(text).toContain('concepts');
    expect(text).not.toContain('Concept overview');
    expect(text).toContain('Docs index');
    expect(el(fixture).textContent).toContain('4 pages'); // overview.md + page.html + page.metadata.json + README.md
    expect(el(fixture).querySelector('[data-testid="project-wiki-node-concepts"]')?.getAttribute('aria-expanded'))
      .toBe('false');

    expandConcepts(fixture);
    text = el(fixture).querySelector('[data-testid="project-wiki-tree"]')?.textContent ?? '';
    expect(text).toContain('Concept overview');
    expect(text).toContain('Page metadata');
    // The nav exposes the physical root path, compact per-document ratings, and subtle file type icons.
    expect(el(fixture).querySelector('[data-testid="project-wiki-root-path"]')!.textContent)
      .toContain('/repo/docs');
    const ratings = el(fixture).querySelector('[data-testid="project-wiki-ratings-concepts/overview.md"]');
    expect(ratings, 'document ratings').toBeTruthy();
    expect(
      ratings!.querySelector('[data-testid="project-wiki-metric-concepts/overview.md-drift"]')?.getAttribute('aria-label')
    ).toBe('Drift B');
    expect(
      ratings!.querySelector('[data-testid="project-wiki-metric-concepts/overview.md-direction"]')?.getAttribute('aria-label')
    ).toBe('Direction Current');
    expect(ratings!.textContent).toContain('B');
    expect(ratings!.textContent).toContain('Now');
    expect(ratings!.textContent).not.toContain('D:');
    expect(ratings!.textContent).not.toContain('Dir:');
    expect(ratings!.textContent).not.toContain('DriftB');
    expect(ratings!.textContent).not.toContain('DirectionCurrent');
    expect(ratings!.textContent).not.toContain('S 76');
    expect(ratings!.textContent).not.toContain('Q B');
    expect(
      el(fixture)
        .querySelector('[data-testid="project-wiki-metric-concepts/page.html-unscored"]')
        ?.getAttribute('aria-label')
    ).toBe('Metadata unscored');
    expect(el(fixture).querySelector('[data-file-type="html"]')).toBeTruthy();
    expect(el(fixture).querySelector('[data-file-type="json"]')).toBeTruthy();
    http.verify();
  });

  it('renders classification chips on page rows: status + compact type code, nothing when unclassified', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);

    const strip = el(fixture).querySelector('[data-testid="project-wiki-class-concepts/overview.md"]');
    expect(strip, 'classification strip').toBeTruthy();
    const status = strip!.querySelector('[data-testid="project-wiki-class-concepts/overview.md-status"]');
    expect(status?.textContent?.trim()).toBe('überholt');
    expect(status?.getAttribute('data-tone')).toBe('superseded');
    const type = strip!.querySelector('[data-testid="project-wiki-class-concepts/overview.md-type"]');
    expect(type?.textContent?.trim()).toBe('KON');
    expect(type?.getAttribute('data-tone')).toBe('muted');

    // The analysis date lives in the tooltips, not as visible column text.
    expect(strip!.textContent).not.toContain('2026');

    // Unclassified page: no classification strip at all.
    expect(el(fixture).querySelector('[data-testid="project-wiki-class-concepts/page.html"]')).toBeNull();
    http.verify();
  });

  it('shows the classification block in the meta panel and opens the successor via the supersededBy link', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const panel = el(fixture).querySelector('[data-testid="project-wiki-classification-panel"]');
    expect(panel, 'classification block').toBeTruthy();
    expect(panel!.textContent).toContain('Klassifikation');

    // Status chip in tree optics; the successor renders as a navigable link.
    const chip = panel!.querySelector('[data-testid="project-wiki-classification-status-chip"]');
    expect(chip?.textContent?.trim()).toBe('überholt');
    expect(chip?.getAttribute('data-tone')).toBe('superseded');

    // Type is spelled out, not the compact tree code; analysis date is visible.
    const type = panel!.querySelector('[data-testid="project-wiki-classification-type"]');
    expect(type?.textContent).toContain('Konzept');
    expect(type?.textContent).not.toContain('KON');
    const analyzed = panel!.querySelector('[data-testid="project-wiki-classification-analyzed"]');
    expect(analyzed?.textContent).toContain('Analyse');
    expect(analyzed?.textContent).toContain('18.07.2026');

    // Clicking the supersededBy link opens the successor page via openFile.
    const link = panel!.querySelector<HTMLButtonElement>('[data-testid="project-wiki-classification-superseded-link"]');
    expect(link?.textContent?.trim()).toBe('concepts/new-overview.md');
    link!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/new-overview.md')
      .flush({ relPath: 'concepts/new-overview.md', content: '# New overview' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/new-overview.md')
      .flush({ ...HISTORY, relPath: 'concepts/new-overview.md' });
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBe('concepts/new-overview.md');
    // The successor is not in the tree / unclassified, so the block hides again.
    expect(el(fixture).querySelector('[data-testid="project-wiki-classification-panel"]')).toBeNull();
    http.verify();
  });

  it('shows total, last-read time, and recent task history in the meta panel', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const panel = el(fixture).querySelector('[data-testid="project-wiki-agent-reads-panel"]');
    expect(panel, 'agent reads panel').toBeTruthy();
    expect(panel!.querySelector('[data-testid="project-wiki-agent-reads-total"]')!.textContent?.trim())
      .toBe('23');
    expect(panel!.querySelector('[data-testid="project-wiki-agent-reads-last"]')!.textContent)
      .toContain('2026');
    expect(panel!.querySelector('[data-testid="project-wiki-agent-reads-recent"]')!.textContent)
      .toContain('AGT-2242');
    expect(panel!.querySelector('[data-testid="project-wiki-agent-reads-recent"]')!.textContent)
      .toContain('AGT-2200');
    http.verify();
  });

  it('hides the classification block for a page without classification', async () => {
    const { fixture, http } = await setup();
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-README.md"]')!
      .click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', content: '# Index' });
    http.expectOne('/api/projects/Demo/wiki/history/README.md')
      .flush({ ...HISTORY, relPath: 'README.md' });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-meta-panel"]'), 'meta panel').toBeTruthy();
    expect(el(fixture).querySelector('[data-testid="project-wiki-classification-panel"]')).toBeNull();
    http.verify();
  });

  it('shows applicable repository style guides and opens one in the Wiki reader', async () => {
    const { fixture, http } = await setup();

    const panel = el(fixture).querySelector('[data-testid="project-wiki-style-guides"]');
    expect(panel?.textContent).toContain('Engineering style guides');
    expect(panel?.textContent).toContain('Angular');
    expect(panel?.textContent).toContain('.NET backend guide');
    expect(panel?.textContent).toContain('Prompt context · v1');

    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-style-guide-angular-components"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/quality/angular-components.md')
      .flush({ relPath: 'quality/angular-components.md', content: '# Angular component guide' });
    http.expectOne('/api/projects/Demo/wiki/history/quality/angular-components.md').flush(HISTORY);
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBe('quality/angular-components.md');
    expect(el(fixture).textContent).toContain('Angular component guide');
    http.verify();
  });

  it('loads a document and its history on click', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const btn = el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]');
    expect(btn).toBeTruthy();
    btn!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    expect(el(fixture).textContent).toContain('Hello wiki');

    // The Source tab exposes a read-only editor surface with line numbers.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-source"]')!.click();
    fixture.detectChanges();
    const source = el(fixture).querySelector('[data-testid="project-wiki-source-editor"]');
    expect(source, 'source editor').toBeTruthy();
    expect(source!.textContent).toContain('# Hello wiki');
    expect(source!.querySelector('[data-testid="project-wiki-source-line"]')!.textContent).toContain('1');

    // The Report tab loads the linked metadata reasoning HTML as a third view.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-report"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md.report.html')
      .flush({
        relPath: 'concepts/overview.md.report.html',
        content: '<main><h1>Concept overview report</h1><p>Why drift: sampled evidence.</p></main>',
      });
    fixture.detectChanges();
    const reportFrame = el(fixture).querySelector<HTMLIFrameElement>('[data-testid="project-wiki-report-frame"]');
    expect(reportFrame, 'report iframe').toBeTruthy();
    expect(reportFrame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(reportFrame!.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(reportFrame!.getAttribute('srcdoc') ?? reportFrame!.srcdoc).toContain('Why drift');

    // History is no longer a document tab; it lives in the right context rail.
    const text = el(fixture).textContent ?? '';
    expect(el(fixture).querySelector('[data-testid="project-wiki-history-panel"]')).toBeTruthy();
    expect(text).toContain('Claude Opus 4.8');
    expect(text).toContain('abc1234');
    http.verify();
  });

  it('opens the report tab at the matching heading when a classification chip is clicked', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const metric = el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-metric-concepts/overview.md-drift"]');
    expect(metric).toBeTruthy();
    metric!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md.report.html')
      .flush({
        relPath: 'concepts/overview.md.report.html',
        content: '<!doctype html><html><head><title>r</title></head><body><h2 id="why-drift">Why drift?</h2></body></html>',
      });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-tab-report"]')?.className)
      .toContain('pwiki__tab--active');
    const srcdoc = el(fixture)
      .querySelector<HTMLIFrameElement>('[data-testid="project-wiki-report-frame"]')!
      .getAttribute('srcdoc') ?? '';
    expect(srcdoc).toContain('url=#why-drift');
    http.verify();
  });

  it('opens Markdown edit mode and saves through the wiki file API', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-edit"]')!.click();
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-editor-shell"]')).toBeTruthy();

    fixture.componentInstance.saveWikiContent('# Changed\n');
    const save = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/files/concepts/overview.md');
    expect(save.request.body).toEqual({ content: '# Changed\n' });
    save.flush({
      relPath: 'concepts/overview.md',
      saved: true,
      changed: true,
      sha: 'def123456',
      branch: 'docs-branch',
    });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    fixture.detectChanges();

    expect(fixture.componentInstance.openedContent()).toBe('# Changed\n');
    expect(el(fixture).querySelector('[data-testid="project-wiki-save-result"]')!.textContent)
      .toContain('docs-branch');
    http.verify();
  });

  it('shows last-modified (date, author, commit subject) in the doc header from git metadata', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    // Doc tab is the default: the header surfaces the newest commit's author + subject.
    const meta = el(fixture).querySelector('[data-testid="project-wiki-last-modified"]');
    expect(meta, 'last-modified header').toBeTruthy();
    expect(meta!.querySelector('[data-testid="project-wiki-last-modified-author"]')!.textContent).toContain('bot');
    expect(meta!.querySelector('[data-testid="project-wiki-last-modified-subject"]')!.textContent).toContain('update');
    http.verify();
  });

  it('filters the tree by needle', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.filter.set('concept');
    fixture.detectChanges();
    const text = el(fixture).querySelector('[data-testid="project-wiki-tree"]')?.textContent ?? '';
    expect(text).toContain('Concept overview');
    expect(text).not.toContain('Docs index');
    http.verify();
  });

  it('opens on the generated Pulse landing view and collapses side panels', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // The wiki opens on Pulse (not a page), with its two quick actions + aside.
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')!.textContent)
      .toContain('Pulse');
    expect(root.querySelector('[data-testid="project-wiki-pulse"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-open-first"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-workspace-meta"]')).toBeTruthy();

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-toggle-nav"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-tree"]')).toBeNull();

    // The meta rail folds via its own labelled head toggle (not a top-bar
    // mini-icon): the rail stays mounted as a slim strip, the body hides.
    const metaToggle = root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-meta-toggle"]')!;
    expect(metaToggle, 'meta toggle head').toBeTruthy();
    expect(metaToggle.getAttribute('aria-expanded')).toBe('true');
    metaToggle.click();
    fixture.detectChanges();
    expect(metaToggle.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('#project-wiki-meta-body')!.hasAttribute('hidden')).toBe(true);
    http.verify();
  });

  it('renders the dashboard head stats from the Pulse payload', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Page total comes from the tree, drift numbers from pulse.drift.counts.
    expect(root.querySelector('[data-testid="project-wiki-stat-pages"]')?.textContent).toContain('4');
    const drift = root.querySelector('[data-testid="project-wiki-stat-drift"]')!;
    expect(drift.textContent).toContain('Fresh');
    expect(drift.textContent).toContain('Aging');
    expect(drift.textContent).toContain('Stale');
    // Overall verdict reads as a small icon + label, not a coloured number.
    expect(root.querySelector('[data-testid="project-wiki-pulse-overall"]')?.textContent)
      .toContain('Drift Aging');
    http.verify();
  });

  it('hides the Aufmerksamkeit card when warnings and inbox are clear', async () => {
    const clearPulse: WikiPulse = {
      ...PULSE,
      inbox: { available: true, reason: null, count: 0, items: [] },
      warnings: { available: true, reason: 'All clear.', count: 0, items: [] },
    };
    const { fixture, http } = await setup(TREE, clearPulse);
    const root = el(fixture);

    expect(root.querySelector('[data-testid="project-wiki-pulse-attention"]')).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox"]')).toBeNull();
    // The feed and drift cards stay regardless of the attention state.
    expect(root.querySelector('[data-testid="project-wiki-pulse-feed"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-drift"]')).toBeTruthy();
    http.verify();
  });

  it('renders the Pulse feed, inbox, and drift bar and opens a feed page', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Change feed row carries its task key + area/drift segment renders.
    expect(root.querySelector('[data-testid="project-wiki-pulse-task-concepts/overview.md"]')?.textContent)
      .toContain('AGT-2014');
    expect(root.querySelector('[data-testid="project-wiki-pulse-area-concepts"]')?.textContent)
      .toContain('Aging');
    // Inbox lists the loose page; overall drift chip shows the worst grade.
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox-open-stray.md"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-overall"]')?.textContent).toContain('Aging');

    // Clicking a feed row opens the page in the reader.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-pulse-feed-open-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')?.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  it('restores collapsed panels, selected document, and active tab from localStorage', async () => {
    localStorage.setItem('atp.wikiMetaPanel.v1', 'collapsed');
    localStorage.setItem(wikiStorageKey(), JSON.stringify({
      navCollapsed: true,
      openedRel: 'concepts/overview.md',
      viewerTab: 'source',
      navWidth: 340,
      contextWidth: 360,
      expandedIds: ['concepts'],
    }));

    const { fixture, http } = await setup();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Restored\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const root = el(fixture);
    expect(root.querySelector('[data-testid="project-wiki-tree"]')).toBeNull();
    // The collapsed rail stays mounted (grade badge remains visible); only the
    // meta body folds, and the head reports the collapsed state via aria.
    expect(root.querySelector('[data-testid="project-wiki-meta-panel"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-meta-toggle"]')!.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('#project-wiki-meta-body')!.hasAttribute('hidden')).toBe(true);
    expect(root.querySelector('[data-testid="project-wiki-source-editor"]')!.textContent).toContain('# Restored');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent).toContain('concepts/overview.md');
    expect(fixture.componentInstance.navWidth()).toBe(340);
    expect(fixture.componentInstance.contextWidth()).toBe(360);
    expect(JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}').expandedIds).toContain('concepts');
    http.verify();
  });

  it('surfaces the page drift grade in the meta head, visible even when the rail is collapsed', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    // The grade lifted from the drift-metadata card into the head reads the
    // companion driftGrade (B, "warn" tone) so it is legible at a glance.
    const grade = el(fixture).querySelector('[data-testid="project-wiki-meta-grade"]')!;
    expect(grade.textContent).toContain('B');
    expect(grade.getAttribute('data-tone')).toBe('warn');

    // Collapsing the rail keeps the grade badge mounted in the (now vertical) head.
    fixture.componentInstance.toggleContext();
    fixture.detectChanges();
    const toggle = el(fixture).querySelector('[data-testid="project-wiki-meta-toggle"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(el(fixture).querySelector('[data-testid="project-wiki-meta-grade"]')!.textContent).toContain('B');
    expect(el(fixture).querySelector('#project-wiki-meta-body')!.hasAttribute('hidden')).toBe(true);
    http.verify();
  });

  it('keeps the meta-rail state unchanged while navigating between pages', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const cmp = fixture.componentInstance;

    const openReadmeHistory = () => http.expectOne('/api/projects/Demo/wiki/history/README.md').flush({
      relPath: 'README.md', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });

    // Open overview.md and fold its meta rail.
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(cmp.contextCollapsed()).toBe(false);
    cmp.toggleContext();
    expect(cmp.contextCollapsed()).toBe(true);

    // A different page inherits the current collapsed state.
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-README.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', content: '# Readme\n' });
    openReadmeHistory();
    fixture.detectChanges();
    expect(cmp.contextCollapsed()).toBe(true);

    // Expand the panel on README, then navigate back.
    cmp.toggleContext();
    expect(cmp.contextCollapsed()).toBe(false);

    // Page navigation does not restore overview.md's former collapsed state.
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(cmp.contextCollapsed()).toBe(false);

    expect(metaPanelStorage().collapsed).toBe(false);
    http.verify();
  });

  it('defaults the meta rail to expanded when no preference is stored', async () => {
    const { fixture, http } = await setup();

    expect(localStorage.getItem('atp.wikiMetaPanel.v1')).toBeNull();
    expect(fixture.componentInstance.contextCollapsed()).toBe(false);
    expect(el(fixture).querySelector('[data-testid="project-wiki-meta-toggle"]')?.getAttribute('aria-expanded'))
      .toBe('true');
    http.verify();
  });

  it('round-trips the meta-rail preference through localStorage', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.toggleContext();
    expect(metaPanelStorage().collapsed).toBe(true);
    fixture.destroy();

    const reloaded = TestBed.createComponent(ProjectWikiSectionComponent);
    reloaded.componentRef.setInput('projectName', 'Demo');
    reloaded.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    flushGradingContext(http);
    reloaded.detectChanges();
    flushStyleGuidesIfRendered(http);
    flushWikiHomeIfRendered(http);
    reloaded.detectChanges();

    expect(reloaded.componentInstance.contextCollapsed()).toBe(true);
    expect(el(reloaded).querySelector('[data-testid="project-wiki-meta-toggle"]')?.getAttribute('aria-expanded'))
      .toBe('false');
    http.verify();
  });

  it('defaults Linked elements open and History closed, with Linked elements first', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>(
      '[data-testid="project-wiki-file-concepts/overview.md"]',
    )!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const root = el(fixture);
    const linked = root.querySelector<HTMLElement>('[data-testid="project-wiki-linked-elements"]')!;
    const history = root.querySelector<HTMLElement>('[data-testid="project-wiki-history-panel"]')!;
    expect(linked.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
    expect(root.querySelector('[data-testid="project-wiki-section-toggle-linked-elements"]')
      ?.getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelector('[data-testid="project-wiki-section-toggle-history"]')
      ?.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('#project-wiki-section-linked-elements')?.hasAttribute('hidden')).toBe(false);
    expect(root.querySelector('#project-wiki-section-history')?.hasAttribute('hidden')).toBe(true);
    http.verify();
  });

  it('round-trips independent meta-section states through the shared storage key', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>(
      '[data-testid="project-wiki-file-concepts/overview.md"]',
    )!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const root = el(fixture);
    root.querySelector<HTMLButtonElement>(
      '[data-testid="project-wiki-section-toggle-linked-elements"]',
    )!.click();
    root.querySelector<HTMLButtonElement>(
      '[data-testid="project-wiki-section-toggle-history"]',
    )!.click();
    expect(metaPanelStorage().sections?.['linkedElements']).toBe(true);
    expect(metaPanelStorage().sections?.['history']).toBe(false);
    fixture.destroy();

    const reloaded = TestBed.createComponent(ProjectWikiSectionComponent);
    reloaded.componentRef.setInput('projectName', 'Demo');
    reloaded.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    flushGradingContext(http);
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Restored overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    reloaded.detectChanges();
    flushStyleGuidesIfRendered(http);
    flushWikiHomeIfRendered(http);
    reloaded.detectChanges();

    expect(el(reloaded).querySelector('[data-testid="project-wiki-section-toggle-linked-elements"]')
      ?.getAttribute('aria-expanded')).toBe('false');
    expect(el(reloaded).querySelector('[data-testid="project-wiki-section-toggle-history"]')
      ?.getAttribute('aria-expanded')).toBe('true');
    http.verify();
  });

  it('opens linked wiki pages in place and task references in task detail', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>(
      '[data-testid="project-wiki-file-concepts/overview.md"]',
    )!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md').flush({
      relPath: 'concepts/overview.md',
      content: '# Overview\n\n[Docs index](../README.md)\n[AGT-2050](task:AGT-2050)',
    });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const root = el(fixture);
    const linkedElements = [...root.querySelectorAll<HTMLAnchorElement>(
      '[data-testid="project-wiki-linked-element"]',
    )];
    const taskLink = linkedElements.find(link => link.title === 'Open task AGT-2050')!;
    expect(taskLink).toBeTruthy();
    taskLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('PROJ-001::agt-2050');

    const wikiLink = linkedElements.find(link => link.title === 'Open wiki page: Docs index')!;
    expect(wikiLink).toBeTruthy();
    wikiLink.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', content: '# Docs index' });
    http.expectOne('/api/projects/Demo/wiki/history/README.md').flush({
      ...HISTORY,
      relPath: 'README.md',
      commits: [],
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBe('README.md');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')?.textContent)
      .toContain('README.md');
    http.verify();
  });

  it('restores the report tab and loads the linked reasoning report from localStorage', async () => {
    localStorage.setItem(wikiStorageKey(), JSON.stringify({
      openedRel: 'concepts/overview.md',
      viewerTab: 'report',
      expandedIds: ['concepts'],
    }));

    const { fixture, http } = await setup();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Restored report\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md.report.html')
      .flush({
        relPath: 'concepts/overview.md.report.html',
        content: '<main><h1>Restored reasoning report</h1></main>',
      });
    fixture.detectChanges();

    const root = el(fixture);
    expect(root.querySelector('[data-testid="project-wiki-tab-report"]')?.className)
      .toContain('pwiki__tab--active');
    expect(root.querySelector('[data-testid="project-wiki-report-frame"]')!).toBeTruthy();
    http.verify();
  });

  it('resizes and persists the tree and context panel widths', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;

    cmp.startPanelResize(fakePointer(100), 'nav');
    cmp.resizePanel(fakePointer(150));
    cmp.finishPanelResize(fakePointer(150));
    expect(cmp.navWidth()).toBe(336);

    cmp.startPanelResize(fakePointer(300), 'context');
    cmp.resizePanel(fakePointer(250));
    cmp.finishPanelResize(fakePointer(250));
    expect(cmp.contextWidth()).toBe(334);

    const stored = JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}') as {
      navWidth?: number;
      contextWidth?: number;
    };
    expect(stored.navWidth).toBe(336);
    expect(stored.contextWidth).toBe(334);

    cmp.onPanelSplitterKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), 'nav');
    expect(cmp.navWidth()).toBe(352);
    cmp.onPanelSplitterKeydown(new KeyboardEvent('keydown', { key: 'ArrowLeft' }), 'context');
    expect(cmp.contextWidth()).toBe(350);
    http.verify();
  });

  it('opens the drift modal with model selection and a document-scoped prompt', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-drift-open-empty"]')!.click();
    fixture.detectChanges();

    http.expectOne('/api/cli/claude/models').flush({
      models: [
        { id: 'claude-opus-4-8', label: 'Claude Opus 4.8', multiplier: null, vendor: 'anthropic', isDefault: true },
      ],
      source: 'test',
    });
    http.expectOne('/api/watch-paths').flush([
      { name: 'Demo', path: 'C:\\repo\\projects\\agent-taskboard' },
    ]);
    http.expectOne(req =>
      req.method === 'GET' && req.urlWithParams.startsWith('/api/drift/agent-taskboard/reports?')
    ).flush({ reports: [] });
    http.expectOne('/api/drift/agent-taskboard/actions/software-architecture-drift/prompt')
      .flush({
        project: 'agent-taskboard',
        capturedAt: '2026-06-11T00:00:00Z',
        architectureModelFound: true,
        architectureModelSourcePath: 'docs/system/architecture/model.md',
        architectureModelRejectionReason: null,
        docs: ['docs/system/architecture/model.md'],
        sourceTree: [],
        moduleBoundaries: [],
        schemas: [],
        testDirs: [],
        recentTasks: [],
        recentDriftReports: [],
        recentAnalysisReports: [],
        prompt: 'Base architecture drift prompt',
      });
    fixture.detectChanges();

    const modal = document.querySelector<HTMLElement>('[data-testid="project-wiki-drift-modal"]');
    expect(modal, 'drift modal').toBeTruthy();
    expect(modal!.querySelector('[data-testid="project-wiki-drift-model"]')!.textContent)
      .toContain('Claude Opus 4.8');
    expect(modal!.querySelector('[data-testid="project-wiki-drift-result"]')!.textContent)
      .toContain('Knowledge page drift analysis');
    expect(modal!.querySelector('[data-testid="project-wiki-drift-result"]')!.textContent)
      .toContain('Base architecture drift prompt');
    http.verify();
  });

  it('opens a TEXT-ONLY right-click context menu for files and folders', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    expandConcepts(fixture);

    const openCtx = (id: string) => {
      const row = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
      expect(row, `row ${id}`).toBeTruthy();
      row!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }));
      fixture.detectChanges();
    };

    const assertTextOnly = (panel: HTMLElement) => {
      expect(panel.querySelectorAll('img')).toHaveLength(0);
      expect(panel.querySelectorAll('svg')).toHaveLength(0);
      expect(panel.querySelectorAll('.app-menu__icon')).toHaveLength(0);
    };

    // File context menu: explicit new tab + file actions, text-only.
    openCtx('concepts/overview.md');
    let panel = document.querySelector<HTMLElement>('[data-testid="wiki-ctx-panel"]');
    expect(panel, 'file context menu panel').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-open-new-tab"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-copy-link"]')!.textContent).toContain('Link kopieren');
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-history"]')).toBeTruthy();
    assertTextOnly(panel!);

    fixture.componentInstance.closeMenu();
    fixture.detectChanges();

    // Category context menu: New page + New category + Link kopieren + Rename + Delete category, text-only.
    openCtx('concepts');
    panel = document.querySelector<HTMLElement>('[data-testid="wiki-ctx-panel"]');
    expect(panel, 'folder context menu panel').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-page"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-folder"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-copy-link"]')!.textContent).toContain('Link kopieren');
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]')!.textContent).toContain('Delete');
    assertTextOnly(panel!);
    http.verify();
  });

  /** Right-click a tree row and invoke its Delete action, confirming the dialog. */
  function deleteViaContextMenu(
    fixture: { detectChanges: () => void },
    root: HTMLElement,
    id: string,
  ): void {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const row = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
    expect(row, `row ${id}`).toBeTruthy();
    row!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }));
    fixture.detectChanges();
    document.querySelector<HTMLButtonElement>('[data-testid="wiki-ctx-item-delete"]')!.click();
    fixture.detectChanges();
    confirmSpy.mockRestore();
  }

  it('deleting the open page steers to the parent folder overview in place and drops its star', async () => {
    setWikiHash();
    const { fixture, http } = await setup();
    const stars = TestBed.inject(WikiStarsService);
    const root = el(fixture);
    expandConcepts(fixture);

    // Open the page and star it.
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    flushDoc(http, 'concepts/overview.md');
    fixture.detectChanges();
    stars.star('Demo', 'concepts/overview.md', 'Concept overview');
    expect(stars.isStarred('Demo', 'concepts/overview.md')).toBe(true);
    expect(window.location.hash).toBe(`${WIKI_BASE_HASH}?page=concepts%2Foverview.md`);

    deleteViaContextMenu(fixture, root, 'concepts/overview.md');

    // The delete resolves; the re-read is soft (no full-flush placeholder).
    http.expectOne(req => req.method === 'DELETE'
      && req.url === '/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', sha: 'dead' });
    expect(fixture.componentInstance.loading()).toBe(false);
    // The tree stayed on screen throughout (never swapped for "Loading...").
    expect(root.querySelector('[data-testid="project-wiki-tree"]')).toBeTruthy();

    // Soft tree re-read (page gone) + the parent folder overview it steered to.
    http.expectOne('/api/projects/Demo/wiki/tree').flush({
      ...TREE,
      root: [
        { ...TREE.root[0], children: TREE.root[0].children.filter(c => c.relPath !== 'concepts/overview.md') },
        TREE.root[1],
      ],
    });
    flushWikiPulse(http);
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBeNull();
    expect(fixture.componentInstance.selectedFolderRel()).toBe('concepts');
    expect(root.querySelector('[data-testid="wiki-folder-view"]')).toBeTruthy();
    expect(window.location.hash).toBe(`${WIKI_BASE_HASH}?folder=concepts`);
    // The dead star is gone so the landing never renders it again.
    expect(stars.isStarred('Demo', 'concepts/overview.md')).toBe(false);
    http.verify();
  });

  it('deleting a different, unopened page leaves the current view untouched', async () => {
    setWikiHash();
    const { fixture, http } = await setup();
    const root = el(fixture);
    expandConcepts(fixture);

    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    flushDoc(http, 'concepts/overview.md');
    fixture.detectChanges();

    // Delete README.md while overview.md is the open page.
    deleteViaContextMenu(fixture, root, 'README.md');
    http.expectOne(req => req.method === 'DELETE'
      && req.url === '/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', sha: 'dead' });
    http.expectOne('/api/projects/Demo/wiki/tree')
      .flush({ ...TREE, root: [TREE.root[0]] });
    flushWikiPulse(http);
    fixture.detectChanges();

    // The open page and its deep-link are untouched; no navigation happened.
    expect(fixture.componentInstance.openedRel()).toBe('concepts/overview.md');
    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    expect(window.location.hash).toBe(`${WIKI_BASE_HASH}?page=concepts%2Foverview.md`);
    http.verify();
  });

  it('deleting the open root page falls back to the landing with the deep-link cleared', async () => {
    setWikiHash();
    const { fixture, http } = await setup();
    const stars = TestBed.inject(WikiStarsService);
    const root = el(fixture);

    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-README.md"]')!.click();
    fixture.detectChanges();
    flushDoc(http, 'README.md');
    fixture.detectChanges();
    stars.star('Demo', 'README.md', 'Docs index');

    deleteViaContextMenu(fixture, root, 'README.md');
    http.expectOne(req => req.method === 'DELETE'
      && req.url === '/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', sha: 'dead' });
    http.expectOne('/api/projects/Demo/wiki/tree')
      .flush({ ...TREE, root: [TREE.root[0]] });
    flushWikiPulse(http);
    fixture.detectChanges();
    // Root page -> landing, which self-fetches its own panels.
    http.match('/api/projects/Demo/style-guides').forEach(r => r.flush(STYLE_GUIDES));
    http.match('/api/projects/Demo/wiki/home').forEach(r => r.flush(HOME));
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBeNull();
    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    expect(window.location.hash).toBe(WIKI_BASE_HASH);
    expect(stars.isStarred('Demo', 'README.md')).toBe(false);
    http.verify();
  });

  it('renders an HTML doc inside a script-enabled opaque-origin sandboxed iframe', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/page.html"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/page.html')
      .flush({
        relPath: 'concepts/page.html',
        content: '<nav><a href="#live">Live</a><a href="#missing">Missing</a></nav><h1 id="live">Sandboxed</h1><a href="./overview.md">Overview</a><script>window.x=1</script>',
      });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/page.html').flush({
      relPath: 'concepts/page.html', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();

    const frame = el(fixture).querySelector<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]');
    expect(frame, 'html iframe').toBeTruthy();
    expect(frame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame!.getAttribute('sandbox')).not.toContain('allow-same-origin');
    const srcdoc = frame!.getAttribute('srcdoc') ?? frame!.srcdoc;
    expect(srcdoc).toContain('Sandboxed');
    expect(srcdoc).toContain(ISOLATED_HTML_LINK_MESSAGE);
    await fixture.whenStable();
    fixture.detectChanges();

    const linkedElements = fixture.debugElement.query(By.directive(WikiLinkedElementsComponent))
      .componentInstance as WikiLinkedElementsComponent;
    linkedElements.onWindowMessage({
      source: frame!.contentWindow,
      data: { type: ISOLATED_HTML_ANCHORS_READY_MESSAGE, anchors: ['live'] },
    } as MessageEvent);
    fixture.detectChanges();

    const anchorLinks = [...el(fixture).querySelectorAll<HTMLAnchorElement>(
      '[data-testid="project-wiki-linked-element"]',
    )];
    const live = anchorLinks.find(link => link.getAttribute('href') === '#live')!;
    const missing = anchorLinks.find(link => link.getAttribute('href') === '#missing')!;
    expect(live.getAttribute('data-anchor-state')).toBe('available');
    expect(missing.getAttribute('data-anchor-state')).toBe('missing');
    expect(missing.getAttribute('aria-disabled')).toBe('true');
    expect(missing.textContent).toContain('Missing');

    const postMessage = vi.spyOn(frame!.contentWindow!, 'postMessage');
    live.click();
    expect(postMessage).toHaveBeenCalledWith({
      type: ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE,
      id: 'live',
    }, '*');
    linkedElements.onWindowMessage({
      source: frame!.contentWindow,
      data: { type: ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE, id: 'live' },
    } as MessageEvent);
    fixture.detectChanges();
    expect(live.getAttribute('data-anchor-state')).toBe('active');
    expect(live.getAttribute('aria-current')).toBe('location');

    fixture.componentInstance.onHtmlFrameMessage({
      source: frame!.contentWindow,
      data: { type: ISOLATED_HTML_LINK_MESSAGE, href: './overview.md' },
    } as MessageEvent);
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Linked overview' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush({
      relPath: 'concepts/overview.md', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.openedRel()).toBe('concepts/overview.md');
    http.verify();
  });

  it('renders JSON metadata in preview and source modes', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/page.metadata.json"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/page.metadata.json')
      .flush({ relPath: 'concepts/page.metadata.json', content: '{"title":"Page metadata","drift":{"grade":"B"}}' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/page.metadata.json').flush({
      relPath: 'concepts/page.metadata.json', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-json-preview"]')!.textContent)
      .toContain('"grade": "B"');

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-source"]')!.click();
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-source-editor"]')!.textContent)
      .toContain('"title":"Page metadata"');
    http.verify();
  });

  it('creates a page via a git-backed POST then re-reads the tree', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.createPage('', 'guide.md');
    fixture.detectChanges();

    const post = http.expectOne(req =>
      req.method === 'POST' && req.url === '/api/projects/Demo/wiki/pages');
    expect(post.request.body).toEqual({ relPath: 'guide.md', content: null });
    post.flush({ relPath: 'docs/guide.md', sha: 'deadbee' });

    // The mutation triggers a refresh of the physical tree.
    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    fixture.detectChanges();
    http.verify();
  });

  it('drag-drops a file onto a folder and moves it via git', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const fileRow = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-README.md"]');
    const folderRow = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-concepts"]');
    expect(fileRow).toBeTruthy();
    expect(folderRow).toBeTruthy();

    const dt = makeDataTransfer();
    fireDrag(fileRow!, 'dragstart', dt);
    fireDrag(folderRow!, 'dragover', dt);
    fireDrag(folderRow!, 'drop', dt);
    fixture.detectChanges();

    const post = http.expectOne(req =>
      req.method === 'POST' && req.url === '/api/projects/Demo/wiki/move');
    expect(post.request.body).toEqual({ fromRelPath: 'README.md', toRelPath: 'concepts/README.md' });
    post.flush({ from: 'README.md', to: 'concepts/README.md', sha: 'abc1234' });

    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    fixture.detectChanges();
    http.verify();
  });

  it('drag-drops a document onto a sibling and persists the file order in place', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const root = el(fixture);
    const overview = root.querySelector<HTMLElement>(
      '[data-testid="project-wiki-node-concepts/overview.md"]')!;
    const html = root.querySelector<HTMLElement>(
      '[data-testid="project-wiki-node-concepts/page.html"]')!;

    const dataTransfer = makeDataTransfer();
    fireDrag(overview, 'dragstart', dataTransfer);
    fireDrag(html, 'dragover', dataTransfer);
    fireDrag(html, 'drop', dataTransfer);
    fixture.detectChanges();

    // The signal-backed tree paints before the request completes.
    const fileOrder = () => [...root.querySelectorAll('[data-testid^="project-wiki-file-concepts/"]')]
      .map(node => node.getAttribute('data-testid'));
    expect(fileOrder()).toEqual([
      'project-wiki-file-concepts/page.html',
      'project-wiki-file-concepts/overview.md',
      'project-wiki-file-concepts/page.metadata.json',
    ]);

    const put = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/file-order');
    expect(put.request.body).toEqual({
      parentRelPath: 'concepts',
      orderedNames: ['page.html', 'overview.md', 'page.metadata.json'],
    });
    put.flush({ relPath: 'app/config/wiki-order.json', sha: 'abc1234' });

    const persistedTree: WikiTree = {
      ...TREE,
      root: [{
        ...TREE.root[0],
        children: [TREE.root[0].children[1], TREE.root[0].children[0], TREE.root[0].children[2]],
      }, TREE.root[1]],
    };
    http.expectOne('/api/projects/Demo/wiki/tree').flush(persistedTree);
    flushWikiPulse(http);
    fixture.detectChanges();

    expect(fileOrder()).toEqual([
      'project-wiki-file-concepts/page.html',
      'project-wiki-file-concepts/overview.md',
      'project-wiki-file-concepts/page.metadata.json',
    ]);
    expect(el(fixture).querySelector('[data-testid="project-wiki-loading"]')).toBeNull();
    http.verify();
  });

  it('drag-drops a folder onto a sibling folder and persists the category order', async () => {
    const { fixture, http } = await setup(REORDER_TREE);
    const root = el(fixture);

    const alpha = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-alpha"]')!;
    const gamma = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-gamma"]')!;
    expect(alpha).toBeTruthy();
    expect(gamma).toBeTruthy();
    // Folder rows are draggable (only frame nodes are not).
    expect(alpha.getAttribute('draggable')).toBe('true');

    // A folder never drops onto a folder with a different parent: no request.
    fixture.componentInstance.toggleExpand('beta');
    fixture.detectChanges();
    const inner = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-beta/inner"]')!;
    expect(inner).toBeTruthy();
    const crossDt = makeDataTransfer();
    fireDrag(alpha, 'dragstart', crossDt);
    fireDrag(inner, 'dragover', crossDt);
    fireDrag(inner, 'drop', crossDt);
    fixture.detectChanges();

    // Dragging alpha down onto sibling gamma: alpha takes the slot after gamma.
    const dt = makeDataTransfer();
    fireDrag(alpha, 'dragstart', dt);
    fireDrag(gamma, 'dragover', dt);
    fireDrag(gamma, 'drop', dt);
    fixture.detectChanges();

    const put = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/folder-order');
    expect(put.request.body).toEqual({ parentRelPath: '', orderedNames: ['beta', 'gamma', 'alpha'] });
    put.flush({ relPath: 'app/config/wiki-order.json', sha: 'abc1234' });

    // The mutation triggers a refresh; the tree comes back in the saved order.
    http.expectOne('/api/projects/Demo/wiki/tree').flush({
      ...REORDER_TREE,
      root: [REORDER_TREE.root[1], REORDER_TREE.root[2], REORDER_TREE.root[0]],
    });
    flushWikiPulse(http);
    fixture.detectChanges();

    const labels = [...root.querySelectorAll('[data-testid^="project-wiki-folder-label-"]')]
      .map(n => (n.getAttribute('data-testid') ?? '').replace('project-wiki-folder-label-', ''))
      .filter(id => !id.includes('/'));
    expect(labels).toEqual(['beta', 'gamma', 'alpha']);
    http.verify();
  });

  it('pins an Overview node above the categories that reopens the dashboard landing', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const node = root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-overview-node"]')!;
    expect(node).toBeTruthy();
    // It sits directly above the sortable category rows and is not draggable.
    expect(node.nextElementSibling?.classList.contains('pwiki__rows')).toBe(true);
    expect(node.getAttribute('draggable')).toBeNull();
    // The landing is the initial view, so the pinned node starts active.
    expect(node.classList.contains('pwiki__overview-node--active')).toBe(true);
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();

    // Opening a document deactivates it.
    expandConcepts(fixture);
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(false);
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeFalsy();

    // Clicking Overview closes the page: the same state as the initial landing.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-overview-node"]')!.click();
    fixture.detectChanges();
    http.match('/api/projects/Demo/style-guides').forEach(r => r.flush(STYLE_GUIDES));
    http.match('/api/projects/Demo/wiki/home').forEach(r => r.flush(HOME));
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBeNull();
    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(true);

    // From a folder overview the node also routes back to the landing.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(false);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-overview-node"]')!.click();
    fixture.detectChanges();
    http.match('/api/projects/Demo/style-guides').forEach(r => r.flush(STYLE_GUIDES));
    http.match('/api/projects/Demo/wiki/home').forEach(r => r.flush(HOME));
    fixture.detectChanges();
    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(true);
    http.verify();
  });

  it('previews an old revision from the history panel', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Current\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    // History is in the right context rail; clicking View fetches the old revision.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-doc-history-view-abc1234"]')!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/revisions/abc/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', sha: 'abc', content: '# Old revision\n' });
    fixture.detectChanges();

    const text = el(fixture).textContent ?? '';
    expect(text).toContain('Old revision');
    expect(el(fixture).querySelector('[data-testid="project-wiki-rev-banner"]')).toBeTruthy();
    http.verify();
  });

  it('supports arrow-key tree navigation and persists expanded folders', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    const tree = root.querySelector<HTMLElement>('[data-testid="project-wiki-tree"]')!;
    expect(tree).toBeTruthy();
    const folder = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-concepts"]')!;
    folder.querySelector<HTMLButtonElement>('.pwiki__label')!.click();
    fixture.detectChanges();
    // Clicking the folder *name* now also selects the folder (overview page).
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();
    await Promise.resolve();

    expect(document.activeElement?.getAttribute('data-testid')).toBe('project-wiki-node-concepts');
    expect(folder.getAttribute('aria-expanded')).toBe('true');

    folder.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    fixture.detectChanges();
    expect(folder.getAttribute('aria-expanded')).toBe('false');

    folder.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="project-wiki-file-concepts/overview.md"]')).toBeTruthy();
    expect(JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}').expandedIds).toContain('concepts');

    folder.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();
    await Promise.resolve();
    expect(document.activeElement?.getAttribute('data-testid')).toBe('project-wiki-node-concepts/overview.md');

    document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Keyboard\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  // ---- folder overview page (content pane) ----

  const FOLDER_CONCEPTS: WikiFolderOverview = {
    path: 'concepts',
    name: 'Concepts',
    children: [
      // Pages deliberately listed before the folder: the view sorts folders first.
      {
        name: 'overview.md', relPath: 'concepts/overview.md', kind: 'page', fileType: 'md',
        title: 'Concept overview', summary: 'Der rote Faden.',
        updatedAt: '2026-07-10T08:00:00Z', size: 2048, childCount: null,
      },
      {
        name: 'page.html', relPath: 'concepts/page.html', kind: 'page', fileType: 'html',
        title: 'HTML page', summary: null,
        updatedAt: '2026-07-01T08:00:00Z', size: 500, childCount: null,
      },
      {
        name: 'deep', relPath: 'concepts/deep', kind: 'folder', fileType: null,
        title: 'Deep dive', summary: 'Unterordner mit Details.',
        updatedAt: '2026-07-11T08:00:00Z', size: null, childCount: 3,
      },
    ],
  };

  it('shows a folder overview when the folder name is clicked and routes row clicks', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Clicking the folder NAME selects it (the chevron would only expand).
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts').flush(FOLDER_CONCEPTS);
    fixture.detectChanges();

    const view = root.querySelector('[data-testid="wiki-folder-view"]')!;
    expect(view, 'folder overview').toBeTruthy();
    // Column headers: Titel | Datei | Typ | Status | Reads | Geändert | Größe.
    const headers = [...view.querySelectorAll('th')].map(th => th.textContent?.trim());
    expect(headers).toEqual(['Titel', 'Datei', 'Typ', 'Status', 'Reads', 'Geändert', 'Größe']);
    // Folders first, then pages in payload order.
    const rowIds = [...view.querySelectorAll('[data-testid^="wiki-folder-row-"]')]
      .map(row => row.getAttribute('data-testid'));
    expect(rowIds).toEqual([
      'wiki-folder-row-concepts/deep',
      'wiki-folder-row-concepts/overview.md',
      'wiki-folder-row-concepts/page.html',
    ]);
    // Type / size formatting incl. child count for folders, summary second line.
    expect(view.querySelector('[data-testid="wiki-folder-type-concepts/deep"]')!.textContent).toContain('Ordner');
    expect(view.querySelector('[data-testid="wiki-folder-size-concepts/deep"]')!.textContent).toContain('3 Einträge');
    expect(view.querySelector('[data-testid="wiki-folder-type-concepts/page.html"]')!.textContent).toContain('html');
    expect(view.querySelector('[data-testid="wiki-folder-size-concepts/overview.md"]')!.textContent).toContain('2.0 KB');
    expect(view.querySelector('[data-testid="wiki-folder-size-concepts/page.html"]')!.textContent).toContain('500 B');
    expect(view.querySelector('[data-testid="wiki-folder-summary-concepts/overview.md"]')!.textContent)
      .toContain('Der rote Faden.');

    // Folder row click drills into the subfolder overview.
    root.querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts/deep').flush({
      path: 'concepts/deep',
      name: 'Deep dive',
      children: [{
        name: 'detail.md', relPath: 'concepts/deep/detail.md', kind: 'page', fileType: 'md',
        title: 'Detail', summary: null, updatedAt: null, size: 10, childCount: null,
      }],
    });
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="wiki-folder-title"]')!.textContent).toContain('Deep dive');
    // The breadcrumb carries the clickable parent segment.
    expect(el(fixture).querySelector('[data-testid="wiki-folder-crumb-concepts"]')).toBeTruthy();

    // Page row click opens the page in the reader.
    el(fixture).querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep/detail.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/deep/detail.md')
      .flush({ relPath: 'concepts/deep/detail.md', content: '# Detail\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/deep/detail.md').flush({
      relPath: 'concepts/deep/detail.md', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/deep/detail.md');
    http.verify();
  });

  it('shows loading and error states for the folder overview', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-folder-loading"]')).toBeTruthy();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-folder-error"]')).toBeTruthy();
    http.verify();
  });

  // ---- shareable deep links (URL <-> open page/folder) ----

  const WIKI_BASE_HASH = '#/projects/demo/wiki';

  /** Put the browser on the wiki rail route so the component owns the URL. */
  function setWikiHash(query = ''): void {
    window.history.replaceState(null, '', `/${WIKI_BASE_HASH}${query}`);
  }

  function flushDoc(http: HttpTestingController, rel: string, content = '# Doc\n'): void {
    http.expectOne(`/api/projects/Demo/wiki/files/${rel}`).flush({ relPath: rel, content });
    http.expectOne(`/api/projects/Demo/wiki/history/${rel}`).flush({ ...HISTORY, relPath: rel });
  }

  it('syncs the URL when a page is opened (history push) and cleared on close', async () => {
    setWikiHash();
    const { fixture, http } = await setup();
    const push = vi.spyOn(window.history, 'pushState');
    expandConcepts(fixture);

    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    flushDoc(http, 'concepts/overview.md');
    fixture.detectChanges();

    // Opening a page is a history entry carrying the encoded relPath.
    expect(push).toHaveBeenCalled();
    expect(window.location.hash).toBe(`${WIKI_BASE_HASH}?page=concepts%2Foverview.md`);

    // Closing the page clears the param back to the bare rail route.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-close"]')!.click();
    fixture.detectChanges();
    http.match('/api/projects/Demo/style-guides').forEach(r => r.flush(STYLE_GUIDES));
    http.match('/api/projects/Demo/wiki/home').forEach(r => r.flush(HOME));
    expect(window.location.hash).toBe(WIKI_BASE_HASH);
    push.mockRestore();
    http.verify();
  });

  it('syncs a folder overview as a replace (no history spam on tree clicks)', async () => {
    setWikiHash();
    const { fixture, http } = await setup();
    const push = vi.spyOn(window.history, 'pushState');
    const replace = vi.spyOn(window.history, 'replaceState');

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts').flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();

    expect(window.location.hash).toBe(`${WIKI_BASE_HASH}?folder=concepts`);
    expect(replace).toHaveBeenCalled();
    expect(push).not.toHaveBeenCalled();
    push.mockRestore();
    replace.mockRestore();
    http.verify();
  });

  it('leaves the URL untouched when mounted off the wiki rail route', async () => {
    // No wiki hash (e.g. studio Hub tab): the component must not hijack the URL.
    window.history.replaceState(null, '', '/');
    const { fixture, http } = await setup();
    const push = vi.spyOn(window.history, 'pushState');
    const replace = vi.spyOn(window.history, 'replaceState');
    expandConcepts(fixture);

    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    flushDoc(http, 'concepts/overview.md');
    fixture.detectChanges();

    expect(window.location.hash).toBe('');
    expect(push).not.toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
    push.mockRestore();
    replace.mockRestore();
    http.verify();
  });

  it('restores an open page from the ?page= URL param on load', async () => {
    setWikiHash('?page=concepts%2Foverview.md');
    const { fixture, http } = await setup();
    flushDoc(http, 'concepts/overview.md', '# Restored via URL\n');
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBe('concepts/overview.md');
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  it('restores and keeps a page on the immutable project-id route', async () => {
    window.history.replaceState(
      null,
      '',
      '/#/projects/PROJ-900/wiki?page=concepts%2Foverview.md',
    );
    const { fixture, http } = await setup(TREE, PULSE, 'PROJ-900');
    flushDoc(http, 'concepts/overview.md', '# Restored via stable id\n');
    fixture.detectChanges();
    flushWikiHomeIfRendered(http);

    expect(fixture.componentInstance.openedRel()).toBe('concepts/overview.md');
    expect(window.location.hash)
      .toBe('#/projects/PROJ-900/wiki?page=concepts%2Foverview.md');
    http.verify();
  });

  it('restores a folder overview from the ?folder= URL param on load', async () => {
    setWikiHash('?folder=concepts');
    const { fixture, http } = await setup();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts').flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedFolderRel()).toBe('concepts');
    expect(el(fixture).querySelector('[data-testid="wiki-folder-view"]')).toBeTruthy();
    http.verify();
  });

  it('falls back to the landing with a dezent hint for an unknown deep-linked path', async () => {
    setWikiHash('?page=ghost%2Fmissing.md');
    const { fixture } = await setup();

    // No page opened: the landing shows, the param is dropped, and a hint names the path.
    expect(fixture.componentInstance.openedRel()).toBeNull();
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();
    const hint = el(fixture).querySelector('[data-testid="project-wiki-deeplink-missing"]');
    expect(hint, 'missing-link hint').toBeTruthy();
    expect(hint!.textContent).toContain('ghost/missing.md');
    expect(hint!.textContent).toContain('Die verlinkte Seite');
    expect(window.location.hash).toBe(WIKI_BASE_HASH);
  });

  it('names a missing folder deep-link a folder, not a page, in the hint', async () => {
    setWikiHash('?folder=ghost-folder');
    const { fixture } = await setup();

    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    const hint = el(fixture).querySelector('[data-testid="project-wiki-deeplink-missing"]');
    expect(hint, 'missing-link hint').toBeTruthy();
    expect(hint!.textContent).toContain('ghost-folder');
    expect(hint!.textContent).toContain('Der verlinkte Ordner');
    expect(window.location.hash).toBe(WIKI_BASE_HASH);
  });

  it('copies an absolute page link from the viewer-header copy icon', async () => {
    setWikiHash();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    flushDoc(http, 'concepts/overview.md');
    fixture.detectChanges();

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-copy-link"]')!.click();
    expect(writeText).toHaveBeenCalledWith(
      `${window.location.origin}/${WIKI_BASE_HASH}?page=concepts%2Foverview.md`,
    );
    http.verify();
  });

  it('copies an absolute folder link from the context menu', async () => {
    setWikiHash();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    const { fixture, http } = await setup();
    fixture.componentInstance.copyWikiLinkForNode({
      name: 'concepts', title: 'concepts', relPath: 'concepts', type: 'folder', children: [],
    });

    expect(writeText).toHaveBeenCalledWith(
      `${window.location.origin}/${WIKI_BASE_HASH}?folder=concepts`,
    );
    http.verify();
  });

  // ---- wiki search ----

  const searchRequest = (http: HttpTestingController, q: string, semantic = false) =>
    http.expectOne(r => r.url === '/api/projects/Demo/wiki/search'
      && r.params.get('q') === q
      && (semantic ? r.params.get('semantic') === 'true' : !r.params.has('semantic')));

  const searchResponse = (overrides: Partial<WikiSearchResponse> = {}): WikiSearchResponse => ({
    query: 'guide',
    semanticUsed: false,
    expandedTerms: [],
    durationMs: 12,
    results: [{
      relPath: 'concepts/overview.md',
      title: 'Concept overview',
      kind: 'md',
      snippet: 'Der <em>Guide</em> für alles',
      score: 0.9,
      updatedAt: '2026-07-10T08:00:00Z',
    }],
    ...overrides,
  });

  it('debounces the search (300ms, min 2 chars) and renders em-snippets safely', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      // A single character never searches.
      cmp.onSearchQueryChange('g');
      vi.advanceTimersByTime(400);
      http.expectNone(r => r.url === '/api/projects/Demo/wiki/search');

      // Retyping inside the window resets the debounce.
      cmp.onSearchQueryChange('gu');
      vi.advanceTimersByTime(200);
      cmp.onSearchQueryChange('guide');
      vi.advanceTimersByTime(299);
      http.expectNone(r => r.url === '/api/projects/Demo/wiki/search');
      vi.advanceTimersByTime(1);

      // The snippet keeps only <em>; injected markup arrives escaped as text.
      searchRequest(http, 'guide').flush(searchResponse({
        results: [{
          relPath: 'concepts/overview.md',
          title: 'Concept overview',
          kind: 'md',
          snippet: 'Der <em>Guide</em> mit <img src=x onerror=alert(1)> und <em onclick=alert(1)>Trick</em>',
          score: 0.9,
          updatedAt: '2026-07-10T08:00:00Z',
        }],
      }));
      fixture.detectChanges();

      const snippet = el(fixture)
        .querySelector('[data-testid="wiki-search-snippet-concepts/overview.md"]')!;
      expect(snippet, 'snippet').toBeTruthy();
      expect(snippet.querySelectorAll('em')).toHaveLength(1);
      expect(snippet.querySelector('em')!.textContent).toBe('Guide');
      expect(snippet.querySelector('img')).toBeNull();
      expect(snippet.textContent).toContain('<img src=x onerror=alert(1)>');
      expect(snippet.textContent).toContain('<em onclick=alert(1)>Trick');
      // Result meta: dimmed relPath + relative time render alongside the title.
      const row = el(fixture).querySelector('[data-testid="wiki-search-open-concepts/overview.md"]')!;
      expect(row.textContent).toContain('Concept overview');
      expect(row.querySelector('code')!.textContent).toContain('concepts/overview.md');
      expect(row.querySelector('time')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('opens the top search hit on Enter', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      const input = root.querySelector<HTMLInputElement>('[data-testid="project-wiki-search"]')!;
      input.value = 'guide';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      fixture.detectChanges();
      vi.advanceTimersByTime(300);
      searchRequest(http, 'guide').flush(searchResponse());
      fixture.detectChanges();

      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      fixture.detectChanges();
      http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
        .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
      http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
      fixture.detectChanges();

      expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
        .toContain('concepts/overview.md');
      // Opening a hit leaves the search: the box is cleared again.
      expect(fixture.componentInstance.searchQuery()).toBe('');
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('Escape cancels a pending search and restores the previous view', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    expandConcepts(fixture);
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      const input = root.querySelector<HTMLInputElement>('[data-testid="project-wiki-search"]')!;
      input.value = 'over';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      fixture.detectChanges();
      // Search view replaces the reader while the query is active...
      expect(root.querySelector('[data-testid="wiki-search-results"]')).toBeTruthy();
      expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')).toBeNull();

      // ...Escape before the debounce fires: no request, previous view returns.
      vi.advanceTimersByTime(100);
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();
      vi.advanceTimersByTime(400);
      http.expectNone(r => r.url === '/api/projects/Demo/wiki/search');
      expect(root.querySelector('[data-testid="wiki-search-results"]')).toBeNull();
      expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
        .toContain('concepts/overview.md');
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('expands the search semantically and shows the expanded terms as chips', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      cmp.onSearchQueryChange('deploy');
      vi.advanceTimersByTime(300);
      searchRequest(http, 'deploy').flush(searchResponse({ query: 'deploy' }));
      fixture.detectChanges();
      // The lexical response never shows the unavailable hint.
      expect(el(fixture).querySelector('[data-testid="wiki-search-semantic-unavailable"]')).toBeNull();

      el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-search-semantic"]')!.click();
      fixture.detectChanges();
      // Spinner while the semantic call runs; the current results stay visible.
      expect(el(fixture).querySelector('[data-testid="wiki-search-semantic-spinner"]')).toBeTruthy();
      expect(el(fixture).querySelector('[data-testid="wiki-search-open-concepts/overview.md"]')).toBeTruthy();

      searchRequest(http, 'deploy', true).flush(searchResponse({
        query: 'deploy',
        semanticUsed: true,
        expandedTerms: ['rollout', 'release'],
        results: [
          ...searchResponse().results,
          {
            relPath: 'ops/rollout.md', title: 'Rollout', kind: 'md',
            snippet: '<em>Rollout</em> Schritte', score: 0.7, updatedAt: null,
          },
        ],
      }));
      fixture.detectChanges();

      const terms = el(fixture).querySelector('[data-testid="wiki-search-expanded-terms"]')!;
      expect(terms, 'expanded terms').toBeTruthy();
      expect(terms.textContent).toContain('rollout');
      expect(terms.textContent).toContain('release');
      expect(el(fixture).querySelector('[data-testid="wiki-search-open-ops/rollout.md"]')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('hints when semantic expansion is unavailable (semanticUsed=false)', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      cmp.onSearchQueryChange('deploy');
      vi.advanceTimersByTime(300);
      searchRequest(http, 'deploy').flush(searchResponse({ query: 'deploy' }));
      fixture.detectChanges();

      el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-search-semantic"]')!.click();
      fixture.detectChanges();
      searchRequest(http, 'deploy', true).flush(searchResponse({ query: 'deploy', semanticUsed: false }));
      fixture.detectChanges();

      expect(el(fixture).querySelector('[data-testid="wiki-search-semantic-unavailable"]')!.textContent)
        .toContain('Semantische Erweiterung nicht verfügbar');
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  // ---- curated landing links ("Einstiege") ----

  it('renders the Einstiege block on top of the landing and routes its links', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const landing = root.querySelector('[data-testid="project-wiki-viewer-empty"]')!;
    const home = landing.querySelector('[data-testid="wiki-home-links"]')!;
    expect(home, 'Einstiege block').toBeTruthy();
    // The block sits at the very top of the landing article, above the Pulse head.
    expect(landing.firstElementChild!.contains(home)).toBe(true);
    expect(home.textContent).toContain('Einstiege');
    expect(home.textContent).toContain('Start');
    expect(home.textContent).toContain('Betrieb');
    // Note renders as a dimmed second line.
    expect(home.textContent).toContain('Der Einstieg');
    // Pulse feed, drift, and inbox remain below.
    expect(root.querySelector('[data-testid="project-wiki-pulse-feed"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-drift"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox"]')).toBeTruthy();

    // A dangling link is dimmed and never navigates.
    const missing = home.querySelector<HTMLElement>('[data-testid="wiki-home-link-missing/gone.md"]')!;
    expect(missing.classList.contains('whome__link--missing')).toBe(true);
    missing.click();
    fixture.detectChanges();
    http.expectNone(r => r.url.includes('/wiki/files/'));

    // An existing link opens the page via the normal reader flow.
    home.querySelector<HTMLButtonElement>('[data-testid="wiki-home-link-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  // ---- starred documents ("Gestarrt") ----

  it('stars the open page from the viewer head and lists it on the landing until unstarred', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Landing without stars: no Gestarrt section at all.
    expect(root.querySelector('[data-testid="project-wiki-starred"]')).toBeNull();

    // Open a page; the viewer-head toggle starts unstarred.
    expandConcepts(fixture);
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const toggle = () => root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-star-toggle"]')!;
    expect(toggle(), 'viewer-head star toggle').toBeTruthy();
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    toggle().click();
    fixture.detectChanges();
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    // Back on the landing: the Gestarrt block sits above the Einstiege block and
    // shows label + dimmed relPath.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-close"]')!.click();
    fixture.detectChanges();
    flushStyleGuidesIfRendered(http);
    flushWikiHomeIfRendered(http);
    fixture.detectChanges();
    const landing = root.querySelector('[data-testid="project-wiki-viewer-empty"]')!;
    const starred = landing.querySelector('[data-testid="project-wiki-starred"]')!;
    expect(starred, 'Gestarrt block').toBeTruthy();
    expect(landing.firstElementChild!.contains(starred)).toBe(true);
    expect(starred.textContent).toContain('Gestarrt');
    expect(starred.textContent).toContain('Concept overview');
    expect(starred.querySelector('code')!.textContent).toContain('concepts/overview.md');

    // Clicking the entry re-opens the page through the normal reader flow.
    starred.querySelector<HTMLButtonElement>('[data-testid="project-wiki-starred-open-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    // Unstar directly at the landing entry: the whole section disappears.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-close"]')!.click();
    fixture.detectChanges();
    flushStyleGuidesIfRendered(http);
    flushWikiHomeIfRendered(http);
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-starred-remove-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-starred"]')).toBeNull();
    expect(localStorage.getItem('atp.projectWikiStars.v1.Demo')).toBeNull();
    http.verify();
  });

  // ---- Plain folder tree (no pinned frame, no locked nodes) ----

  const FOLDER_TREE: WikiTree = {
    projectName: 'Demo',
    baseDir: '/repo/docs',
    exists: true,
    root: [
      {
        name: 'architecture', title: 'architecture',
        relPath: 'architecture', type: 'folder', children: [
          {
            name: 'index.html', title: 'Architecture',
            relPath: 'architecture/index.html', type: 'html', children: [],
          },
          {
            name: 'adr-0001.md', title: 'ADR 1',
            relPath: 'architecture/adr-0001.md', type: 'md', children: [],
          },
        ],
      },
      {
        name: 'concepts', title: 'concepts',
        relPath: 'concepts', type: 'folder', children: [
          {
            name: 'overview.md', title: 'Concept overview',
            relPath: 'concepts/overview.md', type: 'md', children: [],
          },
        ],
      },
    ],
  };

  function expandFolders(fixture: { componentInstance: ProjectWikiSectionComponent; detectChanges: () => void }): void {
    fixture.componentInstance.toggleExpand('architecture');
    fixture.detectChanges();
  }

  it('renders top-level folders in tree order without a pinned node or lock affordance', async () => {
    const { fixture, http } = await setup(FOLDER_TREE);
    expandFolders(fixture);
    const root = el(fixture);

    // Order is exactly what the backend delivered - no client-side pinning.
    const rows = Array.from(root.querySelectorAll<HTMLElement>('[data-testid^="project-wiki-node-"]'))
      .map(r => r.getAttribute('data-testid'));
    expect(rows[0]).toBe('project-wiki-node-architecture');

    // No node renders a lock affordance anymore.
    expect(root.querySelector('[data-testid^="project-wiki-lock-"]')).toBeNull();
    http.verify();
  });

  it('offers the full context menu on every folder and page', async () => {
    const { fixture, http } = await setup(FOLDER_TREE);
    expandFolders(fixture);
    const root = el(fixture);

    const openCtx = (id: string) => {
      const rowEl = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
      expect(rowEl, `row ${id}`).toBeTruthy();
      rowEl!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }));
      fixture.detectChanges();
    };

    // Every folder offers creation plus rename/delete.
    openCtx('architecture');
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-page"]'), 'new page').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-folder"]'), 'new folder').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]'), 'rename').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]'), 'delete').toBeTruthy();

    fixture.componentInstance.closeMenu();
    fixture.detectChanges();

    // Every page keeps the full menu - there are no read-only shells.
    openCtx('architecture/index.html');
    expect(document.querySelector('[data-testid="wiki-ctx-item-history"]'), 'history').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]'), 'page rename').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]'), 'page delete').toBeTruthy();
    http.verify();
  });

  /** History payload for an HTML page (no git metadata, no commits). */
  function flushHtmlHistory(http: HttpTestingController, rel: string): void {
    http.expectOne(`/api/projects/Demo/wiki/history/${rel}`).flush({
      relPath: rel, model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
  }

  it('renders an HTML page in the interactive isolated iframe', async () => {
    const { fixture, http } = await setup(FOLDER_TREE);
    expandFolders(fixture);
    const root = el(fixture);

    root.querySelector<HTMLButtonElement>(
      '[data-testid="project-wiki-file-architecture/index.html"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/architecture/index.html')
      .flush({
        relPath: 'architecture/index.html',
        content: '<!doctype html><html><body><h1>Architecture</h1></body></html>',
      });
    flushHtmlHistory(http, 'architecture/index.html');
    fixture.detectChanges();

    const frame = root.querySelector<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]');
    expect(frame, 'html iframe').toBeTruthy();
    expect(frame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame!.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(frame!.getAttribute('srcdoc') ?? frame!.srcdoc).toContain('Architecture');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('architecture/index.html');
    http.verify();
  });
});

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import {
  WIKI_SEARCH_ROOT_GROUP_PATH,
  WikiSearchResultsComponent,
} from './wiki-search-results.component';
import { WikiStarsService } from '../wiki-stars.service';
import type { WikiSearchResponse, WikiSearchResult } from '../../../../../models/project-docs.model';

const VIEW_KEY = 'atp.projectWikiSearchView.v1';

function clearStarStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectWikiStars.v1.')) localStorage.removeItem(key);
  }
}

const hit = (relPath: string, score: number, overrides: Partial<WikiSearchResult> = {}): WikiSearchResult => ({
  relPath,
  title: relPath.split('/').pop() ?? relPath,
  kind: 'md',
  snippet: '',
  score,
  updatedAt: null,
  ...overrides,
});

const response = (
  results: WikiSearchResult[],
  overrides: Partial<WikiSearchResponse> = {},
): WikiSearchResponse => ({
  query: 'guide',
  semanticUsed: false,
  expandedTerms: [],
  durationMs: 5,
  results,
  ...overrides,
});

/** Multi-folder fixture: nested chain, sibling folder, root-level hit. */
const RESULTS = [
  hit('wiki/concepts/overview.md', 0.9, { title: 'Overview', updatedAt: '2026-07-10T08:00:00Z' }),
  hit('ops/runbook.md', 0.8),
  hit('wiki/concepts/deep/details.md', 0.7),
  hit('README.md', 0.6),
];

async function setup(
  resp: WikiSearchResponse = response(RESULTS),
  documentOrder: readonly string[] = [],
) {
  await TestBed.configureTestingModule({
    imports: [WikiSearchResultsComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();

  const fixture = TestBed.createComponent(WikiSearchResultsComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('query', resp.query);
  fixture.componentRef.setInput('response', resp);
  fixture.componentRef.setInput('documentOrder', documentOrder);
  fixture.detectChanges();
  return { fixture };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiSearchResultsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(VIEW_KEY);
    clearStarStorage();
  });
  afterEach(() => {
    localStorage.removeItem(VIEW_KEY);
    clearStarStorage();
  });

  it('defaults to the tree view: folder groups with counts, compressed chains, best-score order', async () => {
    const { fixture } = await setup();
    const root = el(fixture);

    const tree = root.querySelector('[data-testid="wiki-search-tree"]')!;
    expect(tree, 'tree view is the default').toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-search-list"]')).toBeNull();

    // "wiki" has a single folder child and no direct hits -> one "wiki/concepts" node.
    expect(root.querySelector('[data-testid="wiki-search-group-wiki"]')).toBeNull();
    const concepts = root.querySelector('[data-testid="wiki-search-group-wiki/concepts"]')!;
    expect(concepts.textContent).toContain('wiki/concepts');
    expect(root.querySelector('[data-testid="wiki-search-count-wiki/concepts"]')!.textContent!.trim())
      .toBe('2');
    // "deep" carries a hit, so it stays its own (non-compressed) child group.
    expect(root.querySelector('[data-testid="wiki-search-count-wiki/concepts/deep"]')!.textContent!.trim())
      .toBe('1');
    expect(root.querySelector('[data-testid="wiki-search-count-ops"]')!.textContent!.trim()).toBe('1');

    // Groups follow the best score of their content; the root-level hit
    // gathers under the synthetic "(Wurzel)" group because folder groups exist.
    const order = [...tree.querySelectorAll('[data-testid^="wiki-search-group-"], [data-testid^="wiki-search-open-"]')]
      .map(node => node.getAttribute('data-testid'));
    expect(order).toEqual([
      'wiki-search-group-wiki/concepts',
      'wiki-search-open-wiki/concepts/overview.md',
      'wiki-search-group-wiki/concepts/deep',
      'wiki-search-open-wiki/concepts/deep/details.md',
      'wiki-search-group-ops',
      'wiki-search-open-ops/runbook.md',
      `wiki-search-group-${WIKI_SEARCH_ROOT_GROUP_PATH}`,
      'wiki-search-open-README.md',
    ]);
    const rootGroup = root.querySelector(`[data-testid="wiki-search-group-${WIKI_SEARCH_ROOT_GROUP_PATH}"]`)!;
    expect(rootGroup.textContent).toContain('(Wurzel)');
    expect(root.querySelector(`[data-testid="wiki-search-count-${WIKI_SEARCH_ROOT_GROUP_PATH}"]`)!.textContent!.trim())
      .toBe('1');
    // The synthetic group tooltips as the wiki root, not as a real folder path.
    expect(fixture.componentInstance.groupTooltip(WIKI_SEARCH_ROOT_GROUP_PATH)).toBe('docs/');
    expect(fixture.componentInstance.groupTooltip('ops')).toBe('ops');

    // Leaves keep the untouched hit rendering (title, dimmed relPath, time).
    const leaf = root.querySelector('[data-testid="wiki-search-open-wiki/concepts/overview.md"]')!;
    expect(leaf.textContent).toContain('Overview');
    expect(leaf.querySelector('code')!.textContent).toContain('wiki/concepts/overview.md');
    expect(leaf.querySelector('time')).toBeTruthy();
  });

  it('keeps the tree flat when only root-level hits exist (no pseudo group)', async () => {
    const { fixture } = await setup(response([hit('README.md', 0.9), hit('CHANGELOG.md', 0.8)]));
    const root = el(fixture);

    expect(root.querySelector('[data-testid="wiki-search-tree"]')).toBeTruthy();
    expect(root.querySelector(`[data-testid="wiki-search-group-${WIKI_SEARCH_ROOT_GROUP_PATH}"]`)).toBeNull();
    expect(root.querySelectorAll('[data-testid^="wiki-search-group-"]')).toHaveLength(0);
    const flat = [...root.querySelectorAll('[data-testid^="wiki-search-open-"]')]
      .map(node => node.getAttribute('data-testid'));
    expect(flat).toEqual(['wiki-search-open-README.md', 'wiki-search-open-CHANGELOG.md']);
  });

  it('uses persisted document order in tree mode while the flat list stays score ordered', async () => {
    const hits = response([
      hit('guides/alpha.md', 0.9),
      hit('guides/beta.md', 0.7),
      hit('guides/gamma.md', 0.5),
    ]);
    const { fixture } = await setup(hits, [
      'guides/gamma.md',
      'guides/alpha.md',
      'guides/beta.md',
    ]);
    const root = el(fixture);

    const renderedHits = () => [...root.querySelectorAll('[data-testid^="wiki-search-open-"]')]
      .map(node => node.getAttribute('data-testid'));
    expect(renderedHits()).toEqual([
      'wiki-search-open-guides/gamma.md',
      'wiki-search-open-guides/alpha.md',
      'wiki-search-open-guides/beta.md',
    ]);

    root.querySelector<HTMLButtonElement>('[data-testid="wiki-search-view-list"]')!.click();
    fixture.detectChanges();
    expect(renderedHits()).toEqual([
      'wiki-search-open-guides/alpha.md',
      'wiki-search-open-guides/beta.md',
      'wiki-search-open-guides/gamma.md',
    ]);
  });

  it('collapses the synthetic root group like any folder group', async () => {
    const { fixture } = await setup();
    const root = el(fixture);

    const group = root.querySelector<HTMLButtonElement>(
      `[data-testid="wiki-search-group-${WIKI_SEARCH_ROOT_GROUP_PATH}"]`)!;
    expect(group.getAttribute('aria-expanded')).toBe('true');
    group.click();
    fixture.detectChanges();
    expect(group.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="wiki-search-open-README.md"]')).toBeNull();
    // Folder subtrees keep rendering.
    expect(root.querySelector('[data-testid="wiki-search-open-ops/runbook.md"]')).toBeTruthy();
  });

  it('collapses and re-expands a group on click; a new response starts fully expanded', async () => {
    const { fixture } = await setup();
    const root = el(fixture);

    const group = () => root.querySelector<HTMLButtonElement>('[data-testid="wiki-search-group-wiki/concepts"]')!;
    expect(group().getAttribute('aria-expanded')).toBe('true');

    group().click();
    fixture.detectChanges();
    expect(group().getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="wiki-search-open-wiki/concepts/overview.md"]')).toBeNull();
    expect(root.querySelector('[data-testid="wiki-search-group-wiki/concepts/deep"]')).toBeNull();
    // Siblings keep rendering.
    expect(root.querySelector('[data-testid="wiki-search-open-ops/runbook.md"]')).toBeTruthy();

    group().click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-search-open-wiki/concepts/overview.md"]')).toBeTruthy();

    // Collapse again, then deliver a fresh response: everything is expanded anew.
    group().click();
    fixture.detectChanges();
    expect(group().getAttribute('aria-expanded')).toBe('false');
    fixture.componentRef.setInput('response', response([...RESULTS]));
    fixture.detectChanges();
    expect(group().getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelector('[data-testid="wiki-search-open-wiki/concepts/overview.md"]')).toBeTruthy();
  });

  it('switches tree|list via the segmented control and persists the choice', async () => {
    const { fixture } = await setup();
    const root = el(fixture);

    expect(root.querySelector('[data-testid="wiki-search-view-tree"]')!.getAttribute('aria-pressed'))
      .toBe('true');
    expect(root.querySelector('[role="group"][aria-label="Darstellung der Suchtreffer"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-search-view-list"]')!.getAttribute('aria-pressed')).toBe('false');

    root.querySelector<HTMLButtonElement>('[data-testid="wiki-search-view-list"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-search-list"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-search-tree"]')).toBeNull();
    expect(root.querySelector('[data-testid="wiki-search-group-wiki/concepts"]')).toBeNull();
    // Flat list keeps the plain score order.
    const flat = [...root.querySelectorAll('[data-testid^="wiki-search-open-"]')]
      .map(node => node.getAttribute('data-testid'));
    expect(flat).toEqual(RESULTS.map(r => `wiki-search-open-${r.relPath}`));
    expect(localStorage.getItem(VIEW_KEY)).toBe('list');

    // A fresh instance starts with the persisted list view.
    const second = TestBed.createComponent(WikiSearchResultsComponent);
    second.componentRef.setInput('query', 'guide');
    second.componentRef.setInput('response', response(RESULTS));
    second.detectChanges();
    expect(el(second).querySelector('[data-testid="wiki-search-list"]')).toBeTruthy();

    // Back to the tree, persisted again.
    root.querySelector<HTMLButtonElement>('[data-testid="wiki-search-view-tree"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-search-tree"]')).toBeTruthy();
    expect(localStorage.getItem(VIEW_KEY)).toBe('tree');
  });

  it('emits openResult for a leaf click in the tree view', async () => {
    const { fixture } = await setup();
    const opened: WikiSearchResult[] = [];
    fixture.componentInstance.openResult.subscribe(result => opened.push(result));

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-search-open-ops/runbook.md"]')!.click();
    expect(opened).toHaveLength(1);
    expect(opened[0].relPath).toBe('ops/runbook.md');
  });

  it('toggles the star on a hit leaf without emitting openResult', async () => {
    const { fixture } = await setup();
    const root = el(fixture);
    const opened: WikiSearchResult[] = [];
    fixture.componentInstance.openResult.subscribe(result => opened.push(result));

    const star = () =>
      root.querySelector<HTMLButtonElement>('[data-testid="wiki-search-star-ops/runbook.md"]')!;
    expect(star(), 'star toggle on the hit leaf').toBeTruthy();
    expect(star().getAttribute('aria-pressed')).toBe('false');

    star().click();
    fixture.detectChanges();
    // The star click never counts as opening the result...
    expect(opened).toEqual([]);
    // ...but persists the star (label = hit title) and renders the active state.
    expect(star().getAttribute('aria-pressed')).toBe('true');
    const stars = TestBed.inject(WikiStarsService);
    expect(stars.entries('Demo')[0]).toMatchObject({ relPath: 'ops/runbook.md', label: 'runbook.md' });

    // The same shared row template carries the star in the flat list view too.
    root.querySelector<HTMLButtonElement>('[data-testid="wiki-search-view-list"]')!.click();
    fixture.detectChanges();
    expect(star().getAttribute('aria-pressed')).toBe('true');
    star().click();
    fixture.detectChanges();
    expect(stars.entries('Demo')).toEqual([]);
    expect(opened).toEqual([]);
  });
});

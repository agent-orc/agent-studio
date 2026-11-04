import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiFolderOpenRequest, WikiFolderViewComponent } from './wiki-folder-view.component';
import { WikiStarsService } from '../wiki-stars.service';
import type { WikiFolderOverview } from '../../../../../models/project-docs.model';

const OVERVIEW: WikiFolderOverview = {
  path: 'concepts/deep',
  name: 'Deep dive',
  children: [
    // Pages deliberately listed before the folder: the view sorts folders first.
    {
      name: 'detail.md', relPath: 'concepts/deep/detail.md', kind: 'page', fileType: 'md',
      title: 'Detail', summary: 'Zweite Zeile.', updatedAt: '2026-07-10T08:00:00Z', updatedAtSource: 'git',
      size: 2048, childCount: null,
      classification: {
        status: 'ueberholt', supersededBy: 'concepts/new.md', type: 'konzept', analyzedAt: '2026-07-18',
      },
      agentReads: {
        total: 23,
        lastReadAt: '2026-07-22T10:15:00Z',
        recent: [{ at: '2026-07-22T10:15:00Z', taskKey: 'AGT-2242' }],
      },
    },
    {
      name: 'viz.html', relPath: 'concepts/deep/viz.html', kind: 'page', fileType: 'html',
      title: 'Visualisierung', summary: null, updatedAt: '2026-07-12T08:00:00Z', updatedAtSource: 'mtime',
      size: 500, childCount: null,
    },
    {
      name: 'sub', relPath: 'concepts/deep/sub', kind: 'folder', fileType: null,
      title: 'Unterbereich', summary: null, updatedAt: '2026-07-11T08:00:00Z', updatedAtSource: 'git',
      size: null, childCount: 1,
    },
  ],
};

async function setup(overview: WikiFolderOverview | 'error' = OVERVIEW) {
  await TestBed.configureTestingModule({
    imports: [WikiFolderViewComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(WikiFolderViewComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('relPath', 'concepts/deep');
  fixture.detectChanges();

  const request = http.expectOne('/api/projects/Demo/wiki/folder/concepts/deep');
  if (overview === 'error') {
    request.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
  } else {
    request.flush(overview);
  }
  fixture.detectChanges();
  return { fixture, http };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

function makeDataTransfer(): DataTransfer {
  const store = new Map<string, string>();
  return {
    dropEffect: 'none',
    effectAllowed: 'all',
    setData(type: string, value: string) { store.set(type, value); },
    getData(type: string) { return store.get(type) ?? ''; },
  } as unknown as DataTransfer;
}

function fireDrag(target: Element, type: string, dataTransfer: DataTransfer): void {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', { value: dataTransfer, configurable: true });
  target.dispatchEvent(event);
}

function clearStarStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectWikiStars.v1.')) localStorage.removeItem(key);
  }
}

describe('WikiFolderViewComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    clearStarStorage();
  });

  it('renders the children as a table (Titel/Datei/Typ/Status/Reads/Geändert/Größe), folders first', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const headers = [...root.querySelectorAll('th')].map(th => th.textContent?.trim());
    expect(headers).toEqual(['Titel', 'Datei', 'Typ', 'Status', 'Reads', 'Geändert', 'Größe']);

    const rowIds = [...root.querySelectorAll('[data-testid^="wiki-folder-row-"]')]
      .map(row => row.getAttribute('data-testid'));
    expect(rowIds).toEqual([
      'wiki-folder-row-concepts/deep/sub',
      'wiki-folder-row-concepts/deep/detail.md',
      'wiki-folder-row-concepts/deep/viz.html',
    ]);

    // Typ column: Ordner / md / html; Größe: child count vs. human-readable bytes.
    expect(root.querySelector('[data-testid="wiki-folder-type-concepts/deep/sub"]')!.textContent).toContain('Ordner');
    expect(root.querySelector('[data-testid="wiki-folder-type-concepts/deep/detail.md"]')!.textContent).toContain('md');
    expect(root.querySelector('[data-testid="wiki-folder-type-concepts/deep/viz.html"]')!.textContent).toContain('html');
    expect(root.querySelector('[data-testid="wiki-folder-size-concepts/deep/sub"]')!.textContent).toContain('1 Eintrag');
    expect(root.querySelector('[data-testid="wiki-folder-size-concepts/deep/detail.md"]')!.textContent).toContain('2.0 KB');
    expect(root.querySelector('[data-testid="wiki-folder-size-concepts/deep/viz.html"]')!.textContent).toContain('500 B');

    // Summary renders as the dimmed second line under the title; relative time has
    // the absolute timestamp as its tooltip source attribute value.
    expect(root.querySelector('[data-testid="wiki-folder-summary-concepts/deep/detail.md"]')!.textContent)
      .toContain('Zweite Zeile.');
    expect(root.querySelector('[data-testid="wiki-folder-row-concepts/deep/detail.md"] time')).toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-folder-mtime-concepts/deep/detail.md"]')).toBeNull();
    const mtimeMarker = root.querySelector('[data-testid="wiki-folder-mtime-concepts/deep/viz.html"]');
    expect(mtimeMarker?.textContent).toBe('*');
    expect(mtimeMarker?.getAttribute('title')).toBe('Filesystem time because no Git date is available yet');
    http.verify();
  });

  it('renders read totals for pages and a dash when no read evidence exists', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    expect(root.querySelector('[data-testid="wiki-folder-reads-concepts/deep/detail.md"]')!.textContent?.trim())
      .toBe('23');
    expect(root.querySelector('[data-testid="wiki-folder-reads-concepts/deep/viz.html"]')!.textContent)
      .toContain('–');
    expect(root.querySelector('[data-testid="wiki-folder-reads-concepts/deep/sub"]')!.textContent)
      .toContain('–');
    http.verify();
  });

  it('renders the Status column with classification chips, dimmed dash otherwise', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // detail.md: superseded status chip + type code, tooltip carries successor + date.
    const statusChip = root.querySelector(
      '[data-testid="wiki-folder-class-concepts/deep/detail.md-status"]')!;
    expect(statusChip.textContent?.trim()).toBe('überholt');
    expect(statusChip.getAttribute('data-tone')).toBe('warning');
    const typeChip = root.querySelector(
      '[data-testid="wiki-folder-class-concepts/deep/detail.md-type"]')!;
    expect(typeChip.textContent?.trim()).toBe('KON');
    expect(typeChip.getAttribute('data-tone')).toBe('muted');

    // viz.html has no classification; folders never have one -> dash placeholder.
    expect(root.querySelector('[data-testid="wiki-folder-class-concepts/deep/viz.html"]')!
      .textContent).toContain('–');
    expect(root.querySelector('[data-testid="wiki-folder-class-concepts/deep/sub"]')!
      .textContent).toContain('–');
    http.verify();
  });

  it('emits folder drill-down, page open, and breadcrumb navigation', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    const cmp = fixture.componentInstance;

    const folders: string[] = [];
    const pages: WikiFolderOpenRequest[] = [];
    let rootRequested = 0;
    cmp.openFolder.subscribe(rel => folders.push(rel));
    cmp.openPage.subscribe(request => pages.push(request));
    cmp.openRoot.subscribe(() => rootRequested++);

    root.querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep/sub"]')!.click();
    expect(folders).toEqual(['concepts/deep/sub']);

    root.querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep/detail.md"]')!.click();
    root.querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep/viz.html"]')!.click();
    expect(pages).toEqual([
      { relPath: 'concepts/deep/detail.md', type: 'md' },
      { relPath: 'concepts/deep/viz.html', type: 'html' },
    ]);

    // Breadcrumb: parent segment is clickable, the current one is not a button.
    root.querySelector<HTMLButtonElement>('button[data-testid="wiki-folder-crumb-concepts"]')!.click();
    expect(folders).toEqual(['concepts/deep/sub', 'concepts']);
    expect(root.querySelector('button[data-testid="wiki-folder-crumb-concepts/deep"]')).toBeNull();
    expect(root.querySelector('[data-testid="wiki-folder-crumb-concepts/deep"]')!.getAttribute('aria-current'))
      .toBe('page');

    root.querySelector<HTMLButtonElement>('[data-testid="wiki-folder-crumb-root"]')!.click();
    expect(rootRequested).toBe(1);

    // The breadcrumb-end copy icon requests a link to the shown folder.
    const copied: string[] = [];
    cmp.copyLink.subscribe(rel => copied.push(rel));
    root.querySelector<HTMLButtonElement>('[data-testid="wiki-folder-copy-link"]')!.click();
    expect(copied).toEqual(['concepts/deep']);
    http.verify();
  });

  it('toggles the star on a page row without opening the page', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    const cmp = fixture.componentInstance;
    const pages: WikiFolderOpenRequest[] = [];
    cmp.openPage.subscribe(request => pages.push(request));

    // Folders carry no star toggle - only documents are starrable.
    expect(root.querySelector('[data-testid="wiki-folder-star-concepts/deep/sub"]')).toBeNull();

    const star = () =>
      root.querySelector<HTMLButtonElement>('[data-testid="wiki-folder-star-concepts/deep/detail.md"]')!;
    expect(star(), 'star toggle on the page row').toBeTruthy();
    expect(star().getAttribute('aria-pressed')).toBe('false');

    // Starring persists via the service and re-renders the toggle as active...
    star().click();
    fixture.detectChanges();
    expect(star().getAttribute('aria-pressed')).toBe('true');
    const stars = TestBed.inject(WikiStarsService);
    expect(stars.isStarred('Demo', 'concepts/deep/detail.md')).toBe(true);
    expect(stars.entries('Demo')[0].label).toBe('Detail');
    // ...and never bubbles into the row click that would open the page.
    expect(pages).toEqual([]);

    star().click();
    fixture.detectChanges();
    expect(star().getAttribute('aria-pressed')).toBe('false');
    expect(stars.entries('Demo')).toEqual([]);
    http.verify();
  });

  it('reorders document rows immediately from the drag handle and emits their names', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    const emitted: string[][] = [];
    fixture.componentInstance.fileOrderChange.subscribe(names => emitted.push(names));

    const handle = root.querySelector<HTMLElement>('[data-testid="wiki-folder-drag-concepts/deep/viz.html"]')!;
    const target = root.querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep/detail.md"]')!;
    expect(handle.getAttribute('draggable')).toBe('true');
    expect(root.querySelector('[data-testid="wiki-folder-drag-concepts/deep/sub"]')).toBeNull();

    const dataTransfer = makeDataTransfer();
    fireDrag(handle, 'dragstart', dataTransfer);
    fireDrag(target, 'dragover', dataTransfer);
    fireDrag(target, 'drop', dataTransfer);
    fixture.detectChanges();

    const rowIds = [...root.querySelectorAll('[data-testid^="wiki-folder-row-"]')]
      .map(row => row.getAttribute('data-testid'));
    expect(rowIds).toEqual([
      'wiki-folder-row-concepts/deep/sub',
      'wiki-folder-row-concepts/deep/viz.html',
      'wiki-folder-row-concepts/deep/detail.md',
    ]);
    expect(emitted).toEqual([['viz.html', 'detail.md']]);
    http.verify();
  });

  it('hides document reorder handles when the wiki source is read-only', async () => {
    const { fixture, http } = await setup();

    fixture.componentRef.setInput('writable', false);
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid^="wiki-folder-drag-"]')).toBeNull();
    http.verify();
  });

  it('mirrors an optimistic tree order without re-fetching the folder', async () => {
    const { fixture, http } = await setup();

    fixture.componentRef.setInput('orderedFileNames', ['viz.html', 'detail.md']);
    fixture.detectChanges();

    const rowIds = [...el(fixture).querySelectorAll('[data-testid^="wiki-folder-row-"]')]
      .map(row => row.getAttribute('data-testid'));
    expect(rowIds).toEqual([
      'wiki-folder-row-concepts/deep/sub',
      'wiki-folder-row-concepts/deep/viz.html',
      'wiki-folder-row-concepts/deep/detail.md',
    ]);
    http.verify();
  });

  it('shows the error state when the folder cannot be loaded and reloads on input change', async () => {
    const { fixture, http } = await setup('error');
    expect(el(fixture).querySelector('[data-testid="wiki-folder-error"]')).toBeTruthy();

    // Changing the folder input refetches.
    fixture.componentRef.setInput('relPath', 'concepts');
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ path: 'concepts', name: 'Concepts', children: [] });
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="wiki-folder-error"]')).toBeNull();
    expect(el(fixture).querySelector('[data-testid="wiki-folder-empty"]')).toBeTruthy();
    http.verify();
  });
});

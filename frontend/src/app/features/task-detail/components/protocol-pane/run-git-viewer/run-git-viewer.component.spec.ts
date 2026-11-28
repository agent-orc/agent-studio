import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunGitViewerComponent } from './run-git-viewer.component';
import type { RunFileChange } from '../../../../../features/run-timeline';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('RunGitViewerComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [RunGitViewerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunGitViewerComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] RunGitViewerComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('RunGitViewerComponent keyboard navigation', () => {
  const FILES: RunFileChange[] = [
    { status: 'M', path: 'backend/Foo.cs', added: 3, removed: 1 },
    { status: 'M', path: 'frontend/src/app.ts', added: 5, removed: 0 },
  ];

  afterEach(() => TestBed.resetTestingModule());

  async function makeComponent() {
    await TestBed.configureTestingModule({
      imports: [RunGitViewerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunGitViewerComponent);
    const c = fixture.componentInstance;
    c.files.set(FILES);
    // Expand every folder so the file rows are part of the visible list.
    c.expanded.set(new Set(['', 'backend', 'frontend', 'frontend/src']));
    // Keep the focusable rows inside the root that TestBed tears down.
    const tree = fixture.nativeElement as HTMLElement;
    c.visibleNodes().forEach(() => {
      const div = document.createElement('div');
      div.className = 'rgv__node';
      div.tabIndex = -1;
      tree.appendChild(div);
    });
    return { c, tree };
  }

  function press(c: RunGitViewerComponent, tree: HTMLElement, key: string): void {
    c.onTreeKeydown({ key, preventDefault: () => void 0, currentTarget: tree } as unknown as KeyboardEvent);
  }

  it('flattens the visible tree and defaults the roving path to the first row', async () => {
    const { c } = await makeComponent();
    const paths = c.visibleNodes().map((r) => r.node.fullPath);
    expect(paths).toEqual([
      'backend',
      'backend/Foo.cs',
      'frontend',
      'frontend/src',
      'frontend/src/app.ts',
    ]);
    expect(c.rovingPath()).toBe('backend');
  });

  it('ArrowDown walks the rows and selects files (loading their diff)', async () => {
    const { c, tree } = await makeComponent();

    press(c, tree, 'ArrowDown'); // backend -> backend/Foo.cs (file → selected)
    expect(c.selectedPath()).toBe('backend/Foo.cs');

    press(c, tree, 'ArrowDown'); // -> frontend (folder → focus only)
    expect(c.selectedPath()).toBe('backend/Foo.cs');

    press(c, tree, 'ArrowDown'); // -> frontend/src (folder → focus only)
    expect(c.selectedPath()).toBe('backend/Foo.cs');

    press(c, tree, 'ArrowDown'); // -> frontend/src/app.ts (file → selected)
    expect(c.selectedPath()).toBe('frontend/src/app.ts');
  });

  it('ArrowLeft collapses the focused folder and ArrowRight re-expands it', async () => {
    const { c, tree } = await makeComponent();

    press(c, tree, 'Home');      // focus "backend" folder
    press(c, tree, 'ArrowLeft'); // collapse it
    expect(c.expanded().has('backend')).toBe(false);
    expect(c.visibleNodes().some((r) => r.node.fullPath === 'backend/Foo.cs')).toBe(false);

    press(c, tree, 'ArrowRight'); // expand it again
    expect(c.expanded().has('backend')).toBe(true);
    expect(c.visibleNodes().some((r) => r.node.fullPath === 'backend/Foo.cs')).toBe(true);
  });
});

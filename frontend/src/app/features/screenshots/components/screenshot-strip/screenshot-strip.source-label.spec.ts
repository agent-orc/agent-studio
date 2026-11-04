import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';

import { ScreenshotStripComponent } from './screenshot-strip.component';
import type { TaskScreenshot } from '../../../../features/screenshots';

/**
 * The provenance label is the trust signal this feature exists to deliver:
 * a reviewer must be able to tell a live-backend shot from a mocked-route
 * one at a glance, and the UI must never invent a source it cannot prove.
 * These render-path tests lock that behaviour for the strip thumbnails -
 * `real` / `mocked` / `composite` show a label (composite spells out its
 * parts), `pinned` identifies the deterministic documentation workspace, and
 * `unlabeled` shows nothing.
 */
function shot(overrides: Partial<TaskScreenshot>): TaskScreenshot {
  return {
    jobId: 'job-1',
    jobTitle: 'Task',
    projectName: 'proj',
    watchPath: '',
    fileName: 'shot.png',
    relativePath: 'results/shot.png',
    url: '/api/tasks/job-1/screenshot?path=shot.png',
    caption: 'shot',
    status: null,
    source: 'unlabeled',
    compositeParts: [],
    localPath: 'C:/x/shot.png',
    timestampUtc: '2026-06-10T10:00:00Z',
    ...overrides,
  };
}

async function render(screenshots: TaskScreenshot[]): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [ScreenshotStripComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ScreenshotStripComponent);
  fixture.componentRef.setInput('screenshots', screenshots);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function sourceLabels(host: HTMLElement): string[] {
  return Array.from(host.querySelectorAll('[data-testid="screenshot-source"]')).map((el) =>
    (el.textContent ?? '').trim(),
  );
}

describe('ScreenshotStripComponent source label', () => {
  it('renders real / mocked labels with a data-source attribute', async () => {
    const host = await render([
      shot({ url: 'u-real', fileName: 'a--real.png', source: 'real' }),
      shot({ url: 'u-mocked', fileName: 'b--mocked.png', source: 'mocked' }),
    ]);

    expect(sourceLabels(host)).toEqual(['real', 'mocked']);
    const mocked = host.querySelector('[data-source="mocked"]');
    expect(mocked).not.toBeNull();
  });

  it('spells out composite part sources', async () => {
    const host = await render([
      shot({
        url: 'u-comp',
        fileName: 'before-after--composite-real-mocked.png',
        source: 'composite',
        compositeParts: ['real', 'mocked'],
      }),
    ]);

    expect(sourceLabels(host)).toEqual(['composite (real, mocked)']);
  });

  it('shows a bare composite label when parts are unknown', async () => {
    const host = await render([
      shot({ url: 'u-c2', source: 'composite', compositeParts: [] }),
    ]);

    expect(sourceLabels(host)).toEqual(['composite']);
  });

  it('renders the pinned documentation-workspace label', async () => {
    const host = await render([
      shot({ url: 'u-pinned', fileName: 'landing-board--pinned.png', source: 'pinned' }),
    ]);

    expect(sourceLabels(host)).toEqual(['pinned']);
    expect(host.querySelector('[data-source="pinned"]')).not.toBeNull();
  });

  it('renders no source label for unlabeled screenshots', async () => {
    const host = await render([shot({ url: 'u-plain', source: 'unlabeled' })]);

    expect(sourceLabels(host)).toEqual([]);
  });
});

describe('ScreenshotStripComponent status indicator', () => {
  it('shows the test status as a toned indicator on the thumbnail', async () => {
    const host = await render([
      shot({ url: '/a', status: 'passed' }),
      shot({ url: '/b', status: 'failed' }),
      shot({ url: '/c', status: 'skipped' }),
      shot({ url: '/d', status: null }),
    ]);
    const statuses = Array.from(host.querySelectorAll<HTMLElement>('[data-testid="screenshot-thumb-status"]'));
    expect(statuses.map(el => [el.dataset['tone'], el.textContent?.trim()])).toEqual([
      ['success', 'Passed'],
      ['danger', 'Failed'],
      ['neutral', 'Skipped'],
    ]);
  });
});

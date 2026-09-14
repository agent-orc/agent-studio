import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiSearchService } from './wiki-search.service';

/**
 * AGT-2819 split the Knowledge search state out of `project-wiki-section.ts`.
 * The two invariants worth pinning are the debounce and the stale-response
 * guard: a slow answer to an abandoned query must never win.
 */
function setup() {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      WikiSearchService,
    ],
  });
  return {
    service: TestBed.inject(WikiSearchService),
    http: TestBed.inject(HttpTestingController),
  };
}

function searchUrl(query: string, semantic = false): string {
  return `/api/projects/demo/wiki/search?q=${encodeURIComponent(query)}`
    + (semantic ? '&semantic=true' : '');
}

afterEach(() => {
  vi.useRealTimers();
  TestBed.resetTestingModule();
});

describe('WikiSearchService', () => {
  it('does not search below the minimum query length', () => {
    vi.useFakeTimers();
    const { service, http } = setup();

    service.onQueryChange('demo', 'a');
    vi.advanceTimersByTime(1000);

    expect(service.active()).toBe(false);
    expect(service.loading()).toBe(false);
    http.verify();
  });

  it('debounces a typed query into one request', () => {
    vi.useFakeTimers();
    const { service, http } = setup();

    service.onQueryChange('demo', 'mo');
    service.onQueryChange('demo', 'mod');
    service.onQueryChange('demo', 'model');
    expect(service.loading()).toBe(true);
    vi.advanceTimersByTime(300);

    http.expectOne(searchUrl('model')).flush({ results: [], expandedTerms: [] });
    expect(service.loading()).toBe(false);
    expect(service.active()).toBe(true);
    http.verify();
  });

  it('a stale response cannot overwrite a newer query', () => {
    vi.useFakeTimers();
    const { service, http } = setup();

    service.onQueryChange('demo', 'model');
    vi.advanceTimersByTime(300);
    const stale = http.expectOne(searchUrl('model'));

    // The operator keeps typing before the first answer lands.
    service.onQueryChange('demo', 'models');
    vi.advanceTimersByTime(300);
    const fresh = http.expectOne(searchUrl('models'));

    stale.flush({ results: [{ relPath: 'stale.md', title: 'Stale', snippet: '', score: 1 }], expandedTerms: [] });
    expect(service.response()).toBeNull();

    fresh.flush({ results: [{ relPath: 'fresh.md', title: 'Fresh', snippet: '', score: 1 }], expandedTerms: [] });
    expect(service.response()?.results[0].relPath).toBe('fresh.md');
    http.verify();
  });

  it('semantic expansion re-runs the current query without the debounce', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    service.onQueryChange('demo', 'model');
    vi.advanceTimersByTime(300);
    http.expectOne(searchUrl('model')).flush({ results: [], expandedTerms: [] });

    service.expandSemantically('demo');

    expect(service.semanticRequested()).toBe(true);
    http.expectOne(searchUrl('model', true)).flush({ results: [], expandedTerms: ['architecture'] });
    expect(service.semanticLoading()).toBe(false);
    http.verify();
  });

  it('reports a failed search in English and clears both loading flags', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    service.onQueryChange('demo', 'model');
    vi.advanceTimersByTime(300);

    http.expectOne(searchUrl('model')).error(new ProgressEvent('error'), { status: 500 });

    expect(service.error()).toBe('Search failed.');
    expect(service.loading()).toBe(false);
    expect(service.semanticLoading()).toBe(false);
    http.verify();
  });

  it('reset drops the query and every derived flag', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    service.onQueryChange('demo', 'model');
    vi.advanceTimersByTime(300);
    http.expectOne(searchUrl('model')).flush({ results: [], expandedTerms: [] });

    service.reset();

    expect(service.query()).toBe('');
    expect(service.response()).toBeNull();
    expect(service.active()).toBe(false);
    expect(service.error()).toBeNull();
    expect(service.semanticRequested()).toBe(false);
    http.verify();
  });

  it('a pending debounce is cancelled by reset', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    service.onQueryChange('demo', 'model');

    service.reset();
    vi.advanceTimersByTime(1000);

    http.verify();
  });

  it('topResult is null until a query is active with a hit', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    expect(service.topResult()).toBeNull();

    service.onQueryChange('demo', 'model');
    vi.advanceTimersByTime(300);
    http.expectOne(searchUrl('model')).flush({
      results: [{ relPath: 'docs/model.md', title: 'Model', snippet: '', score: 1 }],
      expandedTerms: [],
    });

    expect(service.topResult()?.relPath).toBe('docs/model.md');
    http.verify();
  });

  it('rerun repeats the current query immediately, keeping the semantic mode', () => {
    vi.useFakeTimers();
    const { service, http } = setup();
    service.onQueryChange('demo', 'model');
    vi.advanceTimersByTime(300);
    http.expectOne(searchUrl('model')).flush({ results: [], expandedTerms: [] });
    service.expandSemantically('demo');
    http.expectOne(searchUrl('model', true)).flush({ results: [], expandedTerms: [] });

    service.rerun('demo');

    http.expectOne(searchUrl('model', true)).flush({ results: [], expandedTerms: [] });
    http.verify();
  });
});

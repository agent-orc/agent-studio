import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { GlobalSearchComponent } from './global-search.component';
import { GlobalSearchService, GlobalSearchStreamHandlers } from './global-search.service';

/** Captures one in-flight stream so a spec can drive frames by hand. */
class StreamHarness {
  handlers: GlobalSearchStreamHandlers = {};
  signal: AbortSignal | null = null;
  calls = 0;
  private resolve: (() => void) | null = null;

  readonly service = {
    stream: (_q: string, handlers: GlobalSearchStreamHandlers, signal: AbortSignal) => {
      this.calls++;
      this.handlers = handlers;
      this.signal = signal;
      return new Promise<void>(resolve => { this.resolve = resolve; });
    },
  } as unknown as GlobalSearchService;

  settle(): void { this.resolve?.(); }
}

describe('GlobalSearchComponent', () => {
  let fixture: ComponentFixture<GlobalSearchComponent>;
  let component: GlobalSearchComponent;
  let harness: StreamHarness;

  beforeEach(async () => {
    vi.useFakeTimers();
    harness = new StreamHarness();
    await TestBed.configureTestingModule({
      imports: [GlobalSearchComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GlobalSearchService, useValue: harness.service },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(GlobalSearchComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('opens with Ctrl+K and closes with Escape', () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
    expect(component.open()).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.open()).toBe(false);
  });

  it('ranks an exact task key before a title match from in-memory board state', () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-20', title: 'AGT-2034 follow-up', projectName: 'P', state: '2-ready', id: 'a' },
      { taskKey: 'b', key: 'AGT-2034', title: 'Global search', projectName: 'P', state: '3-progress', id: 'b' },
    ] as TaskInfo[]);
    component.query.set('AGT-2034');

    expect(component.taskResults().map(x => x.taskKey)).toEqual(['b', 'a']);
  });

  it('debounces keystrokes into a single request', () => {
    component.onQuery('run');
    component.onQuery('runn');
    component.onQuery('runner');
    expect(harness.calls).toBe(0);

    vi.advanceTimersByTime(250);

    expect(harness.calls).toBe(1);
  });

  it('aborts the in-flight search when the query changes', () => {
    component.onQuery('runner');
    vi.advanceTimersByTime(250);
    const first = harness.signal;
    expect(first?.aborted).toBe(false);

    component.onQuery('quota');

    expect(first?.aborted).toBe(true);
  });

  it('aborts the in-flight search when the palette is closed', () => {
    component.onQuery('runner');
    vi.advanceTimersByTime(250);

    component.close();

    expect(harness.signal?.aborted).toBe(true);
    expect(component.searching()).toBe(false);
  });

  it('tracks each domain separately and reports repository progress', () => {
    component.onQuery('runner');
    vi.advanceTimersByTime(250);

    harness.handlers.start?.({ query: 'runner', repositories: 3, domains: ['tasks', 'commits', 'files'] });
    harness.handlers.chunk?.({
      domain: 'tasks', projectName: '', durationMs: 4, cacheHit: true,
      items: [{ domain: 'tasks', projectName: 'P', projectColor: '#fff', title: 'Runner', subtitle: 'AGT-1', taskKey: 'k' }],
    });

    // Tasks are done while the repository sweep is still running.
    expect(component.statusLabel('tasks')).toBe('1 result');
    expect(component.statusLabel('files')).toBe('0 of 3 repositories');

    harness.handlers.progress?.({ domain: 'files', completed: 2, total: 3 });
    expect(component.statusLabel('files')).toBe('2 of 3 repositories');
    expect(component.searching()).toBe(true);

    harness.handlers.progress?.({ domain: 'files', completed: 3, total: 3 });
    expect(component.domainState().files.status).toBe('done');
  });

  it('appends repository chunks without reordering results already on screen', () => {
    component.onQuery('runner');
    vi.advanceTimersByTime(250);
    harness.handlers.start?.({ query: 'runner', repositories: 2, domains: ['files'] });

    harness.handlers.chunk?.({
      domain: 'files', projectName: 'Alpha', durationMs: 10, cacheHit: false,
      items: [{ domain: 'files', projectName: 'Alpha', projectColor: '#fff', title: 'a.ts', subtitle: 'a.ts', path: 'a.ts' }],
    });
    harness.handlers.chunk?.({
      domain: 'files', projectName: 'Beta', durationMs: 12, cacheHit: true,
      items: [{ domain: 'files', projectName: 'Beta', projectColor: '#fff', title: 'b.ts', subtitle: 'b.ts', path: 'b.ts' }],
    });

    expect(component.remote().files.map(item => item.path)).toEqual(['a.ts', 'b.ts']);
  });

  it('surfaces a degraded domain without failing the whole search', () => {
    component.onQuery('runner');
    vi.advanceTimersByTime(250);
    harness.handlers.start?.({ query: 'runner', repositories: 1, domains: ['commits'] });

    harness.handlers.failure?.({ domain: 'commits', projectName: 'Alpha', message: 'Some results could not be loaded.' });
    harness.handlers.progress?.({ domain: 'commits', completed: 1, total: 1 });

    expect(component.domainState().commits.error).toBe('Some results could not be loaded.');
    expect(component.domainState().files.error).toBeNull();
  });

  it('offers a patience note once a search passes two seconds', () => {
    component.onQuery('runner');
    vi.advanceTimersByTime(250);
    harness.handlers.start?.({ query: 'runner', repositories: 4, domains: ['files'] });
    expect(component.showPatienceNote()).toBe(false);

    // The elapsed clock is a 100 ms interval sampling Date.now().
    vi.advanceTimersByTime(2500);

    expect(component.showPatienceNote()).toBe(true);
  });
});

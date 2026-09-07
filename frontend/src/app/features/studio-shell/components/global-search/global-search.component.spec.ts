import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';
import type { TaskInfo } from '../../../../models/task.model';
import { GlobalSearchComponent } from './global-search.component';
import { GlobalSearchItem, GlobalSearchService, GlobalSearchStreamEvent } from './global-search.service';

/** A stream we can drive frame by frame, plus a record of what was cancelled. */
class FakeGlobalSearchService {
  readonly queries: string[] = [];
  readonly unsubscribed: string[] = [];
  private readonly subjects = new Map<string, Subject<GlobalSearchStreamEvent>>();

  searchStream(query: string): Observable<GlobalSearchStreamEvent> {
    this.queries.push(query);
    const subject = new Subject<GlobalSearchStreamEvent>();
    this.subjects.set(query, subject);
    return new Observable<GlobalSearchStreamEvent>(subscriber => {
      const inner = subject.subscribe(subscriber);
      return () => {
        this.unsubscribed.push(query);
        inner.unsubscribe();
      };
    });
  }

  emit(query: string, event: GlobalSearchStreamEvent): void {
    this.subjects.get(query)?.next(event);
  }
}

function item(overrides: Partial<GlobalSearchItem>): GlobalSearchItem {
  return { domain: 'files', projectName: 'P', projectColor: '#fff', title: 't', subtitle: 's', ...overrides };
}

describe('GlobalSearchComponent', () => {
  let fixture: ComponentFixture<GlobalSearchComponent>;
  let component: GlobalSearchComponent;
  let api: FakeGlobalSearchService;

  /** Groups keyed by domain, so a test can name the row it asserts on. */
  const byDomain = () => Object.fromEntries(component.groups().map(group => [group.domain, group]));

  beforeEach(async () => {
    vi.useFakeTimers();
    api = new FakeGlobalSearchService();
    await TestBed.configureTestingModule({
      imports: [GlobalSearchComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GlobalSearchService, useValue: api },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(GlobalSearchComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    component.close();
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

    expect(component.localTaskResults().map(x => x.taskKey)).toEqual(['b', 'a']);
  });

  it('debounces keystrokes so only the settled query reaches the backend', () => {
    component.onQuery('re');
    vi.advanceTimersByTime(100);
    component.onQuery('read');
    vi.advanceTimersByTime(100);
    component.onQuery('readme');
    expect(api.queries).toEqual([]);

    vi.advanceTimersByTime(250);

    expect(api.queries).toEqual(['readme']);
  });

  it('aborts the in-flight stream when the query changes', () => {
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    expect(api.queries).toEqual(['readme']);

    component.onQuery('license');
    expect(api.unsubscribed).toEqual(['readme']);

    vi.advanceTimersByTime(250);
    expect(api.queries).toEqual(['readme', 'license']);
  });

  it('reports per-domain progress and appends repository results as they arrive', () => {
    component.onQuery('readme');
    vi.advanceTimersByTime(250);

    api.emit('readme', { kind: 'meta', payload: { query: 'readme', repositories: 3 } });
    api.emit('readme', {
      kind: 'tasks',
      payload: { items: [item({ domain: 'tasks', title: 'Card', taskKey: 'k1' })], durationMs: 4, error: null },
    });

    expect(byDomain()['tasks'].state.phase).toBe('done');
    expect(component.statusLabel(byDomain()['tasks'].state)).toBe('1 result');
    expect(component.statusLabel(byDomain()['files'].state)).toBe('Searching 0 of 3 repositories…');

    api.emit('readme', {
      kind: 'repository',
      payload: {
        name: 'alpha', index: 1, total: 3, commits: [], files: [item({ title: 'README.md', subtitle: 'README.md' })],
        durationMs: 12, commitsCache: 'miss', filesCache: 'miss', commitsError: null, filesError: null,
      },
    });
    expect(component.statusLabel(byDomain()['files'].state)).toBe('Searching 1 of 3 repositories…');
    expect(byDomain()['files'].items.map(x => x.title)).toEqual(['README.md']);

    api.emit('readme', {
      kind: 'repository',
      payload: {
        name: 'beta', index: 2, total: 3, commits: [], files: [item({ title: 'README-2.md', subtitle: 'README-2.md' })],
        durationMs: 9, commitsCache: 'hit', filesCache: 'hit', commitsError: null, filesError: null,
      },
    });
    // Appended in arrival order: rows the operator is already reading must not move.
    expect(byDomain()['files'].items.map(x => x.title)).toEqual(['README.md', 'README-2.md']);

    api.emit('readme', { kind: 'done', payload: { durationMs: 40, errors: {} } });
    expect(component.statusLabel(byDomain()['files'].state)).toBe('2 results');
    expect(component.searching()).toBe(false);
  });

  it('surfaces a failed domain without hiding the domains that answered', () => {
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    api.emit('readme', { kind: 'meta', payload: { query: 'readme', repositories: 1 } });
    api.emit('readme', {
      kind: 'tasks',
      payload: { items: [item({ domain: 'tasks', title: 'Card', taskKey: 'k1' })], durationMs: 3, error: null },
    });
    api.emit('readme', {
      kind: 'done',
      payload: { durationMs: 30, errors: { files: 'Some results could not be loaded.' } },
    });

    expect(byDomain()['tasks'].items.length).toBe(1);
    expect(byDomain()['files'].state.phase).toBe('failed');
    expect(component.statusLabel(byDomain()['files'].state)).toBe('Some results could not be loaded.');
    // Commits shares the git settle signal but had no error of its own, so it
    // must read as answered rather than go silent.
    expect(byDomain()['commits'].state.phase).toBe('done');
    expect(component.statusLabel(byDomain()['commits'].state)).toBe('0 results');
  });

  it('attributes a repository failure to the domain that failed', () => {
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    api.emit('readme', { kind: 'meta', payload: { query: 'readme', repositories: 1 } });
    api.emit('readme', {
      kind: 'repository',
      payload: {
        name: 'alpha', index: 1, total: 1,
        commits: [], files: [item({ title: 'README.md', subtitle: 'README.md' })],
        durationMs: 20, commitsCache: 'miss', filesCache: 'miss',
        commitsError: 'Some results could not be loaded.', filesError: null,
      },
    });
    api.emit('readme', { kind: 'done', payload: { durationMs: 25, errors: { commits: 'Some results could not be loaded.' } } });

    expect(component.statusLabel(byDomain()['commits'].state)).toBe('alpha: Some results could not be loaded.');
    expect(byDomain()['files'].state.phase).toBe('done');
    expect(byDomain()['files'].items.map(x => x.title)).toEqual(['README.md']);
  });

  it('marks every unanswered domain as failed when the stream itself dies', () => {
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    api.emit('readme', { kind: 'meta', payload: { query: 'readme', repositories: 2 } });
    api.emit('readme', {
      kind: 'tasks',
      payload: { items: [item({ domain: 'tasks', title: 'Card', taskKey: 'k1' })], durationMs: 3, error: null },
    });

    api.emit('readme', { kind: 'failed' });

    // Tasks already answered and keeps its result; the git rows must not settle
    // to a "0 results" that reads as an answer.
    expect(byDomain()['tasks'].state.phase).toBe('done');
    expect(byDomain()['commits'].state.phase).toBe('failed');
    expect(byDomain()['files'].state.phase).toBe('failed');
    expect(component.statusLabel(byDomain()['files'].state)).toBe('Search is temporarily unavailable.');
    expect(component.searching()).toBe(false);
  });

  it('shows the patience note only after two seconds of searching', () => {
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    expect(component.showPatienceNote()).toBe(false);

    vi.advanceTimersByTime(2_000);

    expect(component.showPatienceNote()).toBe(true);
  });

  it('Escape stops a running search first and closes the palette on the second press', () => {
    component.show();
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    api.emit('readme', { kind: 'meta', payload: { query: 'readme', repositories: 2 } });

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(api.unsubscribed).toEqual(['readme']);
    expect(component.searching()).toBe(false);
    expect(component.open()).toBe(true);
    expect(component.statusLabel(byDomain()['files'].state)).toBe('Cancelled.');

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.open()).toBe(false);
  });

  it('merges server task matches below the instant board matches without duplicating a card', () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-1', title: 'readme rewrite', projectName: 'P', state: '2-ready', id: 'a' },
    ] as TaskInfo[]);
    component.onQuery('readme');
    vi.advanceTimersByTime(250);
    api.emit('readme', {
      kind: 'tasks',
      payload: {
        items: [
          item({ domain: 'tasks', title: 'readme rewrite', taskKey: 'a' }),
          item({ domain: 'tasks', title: 'archived card mentioning readme', taskKey: 'z' }),
        ],
        durationMs: 5, error: null,
      },
    });

    expect(component.taskResults().map(x => x.taskKey)).toEqual(['a', 'z']);
  });
});

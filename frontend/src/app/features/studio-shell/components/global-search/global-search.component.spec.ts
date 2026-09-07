import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { TaskInfo } from '../../../../models/task.model';
import { GlobalSearchComponent } from './global-search.component';
import { GlobalSearchChunk, GlobalSearchService } from './global-search.service';

/** Records what the component asked for and lets the test drive chunk delivery. */
class StreamStub {
  calls: { query: string; signal: AbortSignal }[] = [];
  private emit: ((chunk: GlobalSearchChunk) => void) | null = null;
  private finish: (() => void) | null = null;

  searchStream(query: string, _limit: number, signal: AbortSignal, onChunk: (chunk: GlobalSearchChunk) => void) {
    this.calls.push({ query, signal });
    this.emit = onChunk;
    return new Promise<void>((resolve, reject) => {
      this.finish = resolve;
      signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    });
  }

  send(chunk: Partial<GlobalSearchChunk> & Pick<GlobalSearchChunk, 'domain'>): void {
    this.emit?.({ items: [], repository: null, completed: 1, total: 1, error: null, ...chunk });
  }

  complete(): void {
    this.finish?.();
  }
}

describe('GlobalSearchComponent', () => {
  let fixture: ComponentFixture<GlobalSearchComponent>;
  let component: GlobalSearchComponent;
  let stream: StreamStub;

  beforeEach(async () => {
    stream = new StreamStub();
    await TestBed.configureTestingModule({
      imports: [GlobalSearchComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GlobalSearchService, useValue: stream },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(GlobalSearchComponent);
    component = fixture.componentInstance;
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

  it('appends server-only task hits after the instant board matches', async () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-20', title: 'Runner link health', projectName: 'P', state: '2-ready', id: 'a' },
    ] as TaskInfo[]);
    component.onQuery('runner');
    await sleep(300);

    // The board snapshot cannot see prompt text, so "Quota probe" only exists
    // server-side; the card the board already shows must not be duplicated.
    stream.send({ domain: 'tasks', items: [
      { ...item('tasks', 'Runner link health'), taskKey: 'a' },
      { ...item('tasks', 'Quota probe'), taskKey: 'c' },
    ] });

    expect(component.taskResults().map(x => x.title)).toEqual(['Runner link health', 'Quota probe']);
  });

  it('debounces the request and aborts the superseded one on the next keystroke', async () => {
    component.onQuery('run');
    expect(stream.calls.length).toBe(0);

    await sleep(300);
    expect(stream.calls.map(call => call.query)).toEqual(['run']);
    const first = stream.calls[0].signal;

    component.onQuery('runner');
    expect(first.aborted).toBe(true);

    await sleep(300);
    expect(stream.calls.map(call => call.query)).toEqual(['run', 'runner']);
  });

  it('tracks each domain separately and appends repository chunks as they arrive', async () => {
    component.onQuery('runner');
    await sleep(300);

    stream.send({ domain: 'tasks', items: [item('tasks', 'Runner card')] });
    expect(component.domains().tasks.status).toBe('done');
    expect(component.domains().files.status).toBe('searching');

    stream.send({ domain: 'files', repository: 'A', completed: 1, total: 2, items: [item('files', 'a.md')] });
    expect(component.domains().files.status).toBe('searching');
    expect(component.progressDetail('files', 1)).toBe('1 of 2 repositories');

    stream.send({ domain: 'files', repository: 'B', completed: 2, total: 2, items: [item('files', 'b.md')] });
    expect(component.domains().files.status).toBe('done');
    // Appended in arrival order; the group the operator already sees is not reordered.
    expect(component.remote().files.map(x => x.title)).toEqual(['a.md', 'b.md']);
    expect(component.progressDetail('files', 2)).toBe('2');
  });

  it('reports a failing repository on its own domain without hiding the others', async () => {
    component.onQuery('runner');
    await sleep(300);

    stream.send({ domain: 'tasks', items: [item('tasks', 'Runner card')] });
    stream.send({ domain: 'commits', repository: 'A', completed: 1, total: 1, error: 'Some results could not be loaded.' });

    expect(component.domains().commits.status).toBe('failed');
    expect(component.domains().commits.error).toBe('Some results could not be loaded.');
    expect(component.domains().tasks.status).toBe('done');
  });

  it('marks every unfinished domain done when the stream ends', async () => {
    component.onQuery('runner');
    await sleep(300);

    stream.send({ domain: 'tasks' });
    stream.complete();
    await sleep(0);

    expect(component.searching()).toBe(false);
    expect(component.domains().files.status).toBe('done');
  });

  it('cancels the in-flight search on Escape and only closes on the second press', async () => {
    component.show();
    component.onQuery('runner');
    await sleep(300);
    const signal = stream.calls[0].signal;

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(signal.aborted).toBe(true);
    expect(component.searching()).toBe(false);
    expect(component.open()).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.open()).toBe(false);
  });

  it('does not call the backend below the two character floor', async () => {
    component.onQuery('r');
    await sleep(300);

    expect(stream.calls.length).toBe(0);
    expect(component.domains().tasks.status).toBe('idle');
  });
});

function item(domain: 'tasks' | 'commits' | 'files', title: string) {
  return { domain, projectName: 'P', projectColor: '#fff', title, subtitle: title };
}

function sleep(ms: number) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

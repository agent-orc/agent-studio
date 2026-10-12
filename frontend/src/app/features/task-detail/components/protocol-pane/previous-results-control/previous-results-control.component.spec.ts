import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PreviousResultsControlComponent } from './previous-results-control.component';

describe('PreviousResultsControlComponent', () => {
  const localEntry = {
    id: 'local-0001',
    timestamp: '2026-09-07T06:00:00Z',
    producer: 'run attempt #1',
    producerKind: 'run-attempt',
    lane: '5e-escalated',
    result: 'Success',
    case: 'feature',
    source: 'task-folder',
    version: 1,
  };

  function render() {
    TestBed.configureTestingModule({
      imports: [PreviousResultsControlComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PreviousResultsControlComponent);
    fixture.componentRef.setInput('jobId', 'AGT-2738');
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController) };
  }

  function flushList(http: HttpTestingController, body: unknown[], legacy?: string) {
    http.expectOne(request => request.url === '/api/tasks/AGT-2738/result-history')
      .flush(body, { headers: legacy ? { 'X-Result-History-Legacy': legacy } : {} });
  }

  function openMenu(fixture: { nativeElement: HTMLElement; detectChanges(): void }) {
    const toggle = fixture.nativeElement.querySelector<HTMLButtonElement>('[data-testid="previous-results-toggle"]')!;
    toggle.click();
    fixture.detectChanges();
  }

  it('says older results are still loading instead of claiming there are none, and looks once more on open', () => {
    const { fixture, http } = render();
    flushList(http, [], 'pending');
    fixture.detectChanges();

    openMenu(fixture);
    const menu: HTMLElement = fixture.nativeElement.querySelector('[data-testid="previous-results-list"]');
    expect(menu.textContent).not.toContain('No previous results found.');
    expect(menu.querySelector('[data-testid="previous-results-legacy-hint"]')?.textContent)
      .toContain('Older results are still loading.');

    // The re-fetch on open picks up the finished backfill.
    flushList(http, [{ ...localEntry, id: 'git-a411991f', source: 'workspace-history', version: null }], 'complete');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="previous-result-git-a411991f"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="previous-results-legacy-hint"]')).toBeNull();

    // Only once: closing and reopening does not fetch again.
    openMenu(fixture);
    openMenu(fixture);
    http.verify();
  });

  it('keeps the local results and says older ones could not be loaded when the backfill failed', () => {
    const { fixture, http } = render();
    flushList(http, [localEntry], 'failed');
    fixture.detectChanges();

    openMenu(fixture);
    expect(fixture.nativeElement.querySelector('[data-testid="previous-result-local-0001"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="previous-results-legacy-hint"]')?.textContent)
      .toContain('Older results could not be loaded.');
    expect(fixture.nativeElement.textContent).not.toContain('No previous results found.');
    http.verify();
  });

  it('reports no previous results only once the backfill is complete', () => {
    const { fixture, http } = render();
    flushList(http, [], 'complete');
    fixture.detectChanges();

    openMenu(fixture);
    expect(fixture.nativeElement.textContent).toContain('No previous results found.');
    expect(fixture.nativeElement.querySelector('[data-testid="previous-results-legacy-hint"]')).toBeNull();
    http.verify();
  });

  it('lists prior result metadata and opens one with a clear previous-result banner', () => {
    TestBed.configureTestingModule({
      imports: [PreviousResultsControlComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PreviousResultsControlComponent);
    fixture.componentRef.setInput('jobId', 'AGT-2707');
    fixture.componentRef.setInput('watchPath', '/workspace/demo');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request =>
      request.url === '/api/tasks/AGT-2707/result-history'
      && request.params.get('watchPath') === '/workspace/demo')
      .flush([
        {
          id: 'local-0002',
          timestamp: '2026-09-11T08:21:00Z',
          producer: 'review attempt #2',
          producerKind: 'review-attempt',
          lane: '4-auto-review',
          result: 'Success',
          case: 'blocked',
          source: 'task-folder',
          version: 2,
        },
        {
          id: 'local-0001',
          timestamp: '2026-09-07T06:00:00Z',
          producer: 'run attempt #1',
          producerKind: 'run-attempt',
          lane: '5e-escalated',
          result: 'Success',
          case: 'feature',
          source: 'task-folder',
          version: 1,
        },
      ]);
    fixture.detectChanges();

    const toggle: HTMLButtonElement = fixture.nativeElement.querySelector('[data-testid="previous-results-toggle"]');
    expect(toggle.textContent).toContain('2');
    toggle.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="previous-result-local-"]').length).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('review attempt #2');
    expect(fixture.nativeElement.textContent).toContain('Result: Success');
    expect(fixture.nativeElement.textContent).toContain('Case: blocked');

    const first: HTMLButtonElement = fixture.nativeElement.querySelector('[data-testid="previous-result-local-0001"]');
    first.click();
    http.expectOne(request => request.url === '/api/tasks/AGT-2707/result-history/local-0001')
      .flush({
        entry: {
          id: 'local-0001',
          timestamp: '2026-09-07T06:00:00Z',
          producer: 'run attempt #1',
          producerKind: 'run-attempt',
          lane: '5e-escalated',
          result: 'Success',
          case: 'feature',
          source: 'task-folder',
          version: 1,
        },
        markdown: '# Status\n- Result: Success\n- Case: feature\n',
      });
    fixture.detectChanges();

    const banner: HTMLElement = fixture.nativeElement.querySelector('[data-testid="previous-result-banner"]');
    expect(banner.textContent).toContain('Previous result from');
    expect(banner.textContent).toContain('run attempt #1');
    expect(banner.textContent).toContain('5e-escalated');
    http.verify();
  });
});

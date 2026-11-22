import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { IntegrationConflictsComponent } from './integration-conflicts';

describe('IntegrationConflictsComponent', () => {
  afterEach(() => { TestBed.inject(HttpTestingController).verify(); TestBed.resetTestingModule(); });

  function setup() {
    TestBed.configureTestingModule({ imports: [IntegrationConflictsComponent], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(IntegrationConflictsComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();
    const request = TestBed.inject(HttpTestingController).expectOne(req => req.url === '/api/projects/Agent%20Studio/integration-conflicts');
    expect(request.request.params.has('from')).toBe(true);
    return { fixture, request };
  }

  it('renders the denominator, daily rates, files and card pairs', () => {
    const { fixture, request } = setup();
    request.flush({ summary: { conflicts: 1, integratedCards: 4, per100IntegratedCards: 25,
      topFiles: [{ name: 'Host/Program.cs', count: 1 }], cardPairs: [{ cardA: 'AGT-1', cardB: 'AGT-2', count: 1 }],
      daily: [{ day: '2026-11-22T00:00:00Z', conflicts: 1, integratedCards: 4, per100IntegratedCards: 25 }] } });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('25 conflicts per 100');
    expect(fixture.nativeElement.textContent).toContain('Host/Program.cs');
    expect(fixture.nativeElement.textContent).toContain('AGT-1 / AGT-2');
  });

  it('keeps an empty denominator unavailable', () => {
    const { fixture, request } = setup();
    request.flush({ summary: { conflicts: 0, integratedCards: 0, per100IntegratedCards: null, topFiles: [], cardPairs: [], daily: [] } });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Unavailable');
    expect(fixture.nativeElement.textContent).toContain('History starts when capture is deployed');
  });

  it('shows a recoverable error', () => {
    const { fixture, request } = setup();
    request.flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain('unavailable');
    fixture.componentInstance.retry();
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(req => req.url.includes('/integration-conflicts')).flush({ summary: null });
  });
  it('keeps its request when unrelated interceptor state changes', () => {
    const interceptorState = signal(0);
    TestBed.configureTestingModule({ imports: [IntegrationConflictsComponent], providers: [
      provideHttpClient(withInterceptors([(request, next) => { interceptorState(); return next(request); }])),
      provideHttpClientTesting(),
    ] });
    const fixture = TestBed.createComponent(IntegrationConflictsComponent);
    fixture.componentRef.setInput('projectName', 'Project');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne(req => req.url.includes('/integration-conflicts'));
    interceptorState.set(1);
    fixture.detectChanges();
    expect(request.cancelled).toBe(false);
    request.flush({ summary: { conflicts: 0, integratedCards: 0, per100IntegratedCards: null, topFiles: [], cardPairs: [], daily: [] } });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Unavailable');
  });

});

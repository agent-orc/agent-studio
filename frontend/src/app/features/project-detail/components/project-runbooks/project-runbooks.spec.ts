import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ProjectRunbooksComponent, RUNBOOK_POLL_MS, verificationLabel } from './project-runbooks';

describe('ProjectRunbooksComponent', () => {
  it('shows environment-scoped runbooks and approval history', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectRunbooksComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectRunbooksComponent);
    fixture.componentRef.setInput('projectName', 'runbooks');
    fixture.componentRef.setInput('environmentId', 'production');
    fixture.componentRef.setInput('environmentKind', 'production');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/runbooks/deploy/runbooks').flush([
      { name: 'backup', steps: ['backup'], environments: ['production'] },
      { name: 'stage-only', steps: ['restart'], environments: ['staging'] },
    ]);
    http.expectOne(request => request.url === '/api/projects/runbooks/deploy/runbooks/runs' && request.params.get('environmentId') === 'production').flush([
      { id: 'run-1', name: 'backup', state: 'awaiting-approval', requestedBy: 'operator', approvedBy: null, steps: [] },
    ]);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('backup');
    expect(text).toContain('Owner or Maintainer approval required');
    const state = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-testid="runbook-run-state"]');
    expect(state?.dataset['tone']).toBe('info');
    expect(state?.textContent?.trim()).toBe('awaiting-approval');
    expect(text).not.toContain('stage-only');
  });

  it('posts a run request and approval, then refreshes the environment history', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectRunbooksComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectRunbooksComponent);
    fixture.componentRef.setInput('projectName', 'runbooks');
    fixture.componentRef.setInput('environmentId', 'production');
    fixture.componentRef.setInput('environmentKind', 'production');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/runbooks/deploy/runbooks').flush([
      { name: 'backup', steps: ['backup'], environments: ['production'] },
    ]);
    const history = () => http.expectOne(request => request.url === '/api/projects/runbooks/deploy/runbooks/runs'
      && request.params.get('environmentId') === 'production');
    history().flush([]);
    fixture.detectChanges();

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="runbook-start-backup"]')!.click();
    const start = http.expectOne('/api/projects/runbooks/deploy/environments/production/runbooks/backup/runs');
    expect(start.request.method).toBe('POST');
    start.flush({ id: 'run-1' });
    history().flush([{ id: 'run-1', name: 'backup', state: 'awaiting-approval',
      requestedBy: 'operator', approvedBy: null, steps: [] }]);
    fixture.detectChanges();

    const approve = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
      .find(button => button.textContent?.trim() === 'Approve')!;
    approve.click();
    const approval = http.expectOne('/api/projects/runbooks/deploy/runbooks/runs/run-1/approve');
    expect(approval.request.method).toBe('POST');
    approval.flush({ id: 'run-1' });
    history().flush([{ id: 'run-1', name: 'backup', state: 'queued',
      requestedBy: 'operator', approvedBy: 'owner', steps: [] }]);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('approved by owner');
  });

  afterEach(() => vi.useRealTimers());

  async function mount(runs: unknown[]) {
    await TestBed.configureTestingModule({ imports: [ProjectRunbooksComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectRunbooksComponent);
    fixture.componentRef.setInput('projectName', 'runbooks');
    fixture.componentRef.setInput('environmentId', 'production');
    fixture.componentRef.setInput('environmentKind', 'production');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/runbooks/deploy/runbooks').flush([
      { name: 'restart', steps: ['restart-container'], environments: [], verify: 'health:ready' },
    ]);
    const history = () => http.match(request => request.url === '/api/projects/runbooks/deploy/runbooks/runs');
    history()[0].flush(runs);
    fixture.detectChanges();
    return { fixture, http, history, root: fixture.nativeElement as HTMLElement };
  }

  it('refreshes the history while a run is queued or running and stops once it settles', async () => {
    // M4: the history stayed on queued until a manual Refresh.
    vi.useFakeTimers();
    const queued = { id: 'run-1', name: 'restart', state: 'queued', requestedBy: 'usr_1', requestedByName: 'Olivia Operator',
      approvedBy: null, steps: [] };
    const { fixture, history } = await mount([queued]);

    vi.advanceTimersByTime(RUNBOOK_POLL_MS);
    const first = history();
    expect(first.length).toBe(1);
    first[0].flush([{ ...queued, state: 'running', verifies: 'health check ready' }]);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Checking health check ready once the steps finish');

    vi.advanceTimersByTime(RUNBOOK_POLL_MS);
    history()[0].flush([{ ...queued, state: 'failed', verifies: 'health check ready',
      verification: { target: 'health check ready', passed: false, detail: 'Health check ready did not pass: answered 504.' } }]);
    fixture.detectChanges();
    const verification = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="runbook-verification"]');
    expect(verification?.textContent).toContain('Not verified');
    expect(verification?.textContent).toContain('answered 504');

    vi.advanceTimersByTime(RUNBOOK_POLL_MS * 3);
    expect(history().length).toBe(0);
  });

  it('names people by display name and groups the log by step with its output', async () => {
    const { fixture, http, root } = await mount([{ id: 'run-1', name: 'restart', state: 'succeeded',
      requestedBy: 'usr_ecaff70110', requestedByName: 'Olivia Operator', approvedBy: 'usr_owner1', approvedByName: 'Oscar Owner',
      steps: [{ name: 'restart-container', state: 'succeeded', logRef: '' }, { name: 'verification', state: 'succeeded', logRef: '' }] }]);
    expect(root.textContent).toContain('requested by Olivia Operator, approved by Oscar Owner');
    expect(root.textContent).not.toContain('usr_ecaff70110');
    expect(root.textContent).toContain('Verifies health check ready after the steps');

    root.querySelector<HTMLButtonElement>('[data-testid="runbook-log-toggle-run-1"]')!.click();
    http.expectOne('/api/projects/runbooks/deploy/runbooks/runs/run-1/log').flush({ lines: [
      { step: 'restart-container', message: 'Container demo restarted.' },
      { step: 'restart-container', message: 'Operation succeeded.' },
      { step: 'verification', message: 'Health check ready passed: answered 200 after 41 ms.' },
    ] });
    fixture.detectChanges();
    const restart = root.querySelector('[data-testid="runbook-log-step-restart-container"]')!;
    expect(Array.from(restart.querySelectorAll('.runbooks__line')).map(line => line.textContent)).toEqual([
      'Container demo restarted.', 'Operation succeeded.',
    ]);
    expect(root.querySelector('[data-testid="runbook-log-step-verification"]')?.textContent).toContain('answered 200');
  });

  it('marks the pressed Run button pending until the request settles', async () => {
    const { fixture, http, root } = await mount([]);
    const run = root.querySelector<HTMLButtonElement>('[data-testid="runbook-start-restart"]')!;
    run.click();
    fixture.detectChanges();
    expect(run.getAttribute('aria-busy')).toBe('true');
    expect(run.disabled).toBe(true);
    http.expectOne('/api/projects/runbooks/deploy/environments/production/runbooks/restart/runs').flush({ id: 'run-1' });
    fixture.detectChanges();
    expect(run.getAttribute('aria-busy')).toBeNull();
  });

  it('reads a verify value as the check it names', () => {
    expect(verificationLabel('health:ready')).toBe('health check ready');
    expect(verificationLabel('alert:5xx-rate')).toBe('alert rule 5xx-rate');
    expect(verificationLabel(null)).toBeNull();
  });
});

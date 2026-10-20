import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ProjectRunbooksComponent } from './project-runbooks';

describe('ProjectRunbooksComponent', () => {
  it('shows environment-scoped runbooks and approval history', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectRunbooksComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectRunbooksComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.componentRef.setInput('environmentId', 'production');
    fixture.componentRef.setInput('environmentKind', 'production');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/demo/deploy/runbooks').flush([
      { name: 'backup', steps: ['backup'], environments: ['production'] },
      { name: 'stage-only', steps: ['restart'], environments: ['staging'] },
    ]);
    http.expectOne(request => request.url.endsWith('/runbooks/runs') && request.params.get('environmentId') === 'production').flush([
      { id: 'run-1', name: 'backup', state: 'awaiting-approval', requestedBy: 'operator', approvedBy: null, steps: [] },
    ]);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('backup');
    expect(text).toContain('Owner or Maintainer approval required');
    expect(text).not.toContain('stage-only');
  });

  it('posts a run request for a project named runbooks, then approves and refreshes history', async () => {
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
    const history = () => http.expectOne(request => request.url.endsWith('/runbooks/runs')
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
});

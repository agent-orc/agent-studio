import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../../../models/task.model';
import { QualityStudioService } from '../../../../../services/quality-studio.service';
import { QualityPanelComponent } from './quality-panel.component';

describe('QualityPanelComponent request reuse', () => {
  it('keeps an in-flight quality read when the same task updates', async () => {
    await TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: QualityStudioService, useValue: { openRange: vi.fn() } }],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(QualityPanelComponent);
    const job = { id: 'job-x', taskKey: 'X::job-x', watchPath: '/x', projectName: 'X' } as TaskInfo;
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    const pending = http.expectOne(request => request.url === '/api/tasks/job-x/quality-studio');
    fixture.componentRef.setInput('job', { ...job, title: 'Updated title' });
    fixture.detectChanges();
    expect(pending.cancelled).toBe(false);
    http.expectNone(request => request.url === '/api/tasks/job-x/quality-studio');
    pending.flush({ state: 'not-registered', repositoryId: null, currentHead: null,
      rounds: [], landedSha: null, landedBaseSha: null, landedPatchEqualsReviewedPatch: null });
    expect(fixture.componentInstance.view()?.state).toBe('not-registered');
    http.verify();
  });

  it('keeps a failed read through same-task updates and retries explicitly', async () => {
    await TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: QualityStudioService, useValue: { openRange: vi.fn() } }],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(QualityPanelComponent);
    const job = { id: 'job-x', taskKey: 'X::job-x', watchPath: '/x', projectName: 'X' } as TaskInfo;
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    http.expectOne(request => request.url === '/api/tasks/job-x/quality-studio')
      .flush({ error: 'offline' }, { status: 503, statusText: 'Unavailable' });
    expect(fixture.componentInstance.loadError()).toBe(true);

    fixture.componentRef.setInput('job', { ...job });
    fixture.detectChanges();
    http.expectNone(request => request.url === '/api/tasks/job-x/quality-studio');
    expect(fixture.componentInstance.loadError()).toBe(true);

    fixture.componentInstance.retry();
    fixture.detectChanges();
    http.expectOne(request => request.url === '/api/tasks/job-x/quality-studio').flush({
      state: 'not-registered', repositoryId: null, currentHead: null, rounds: [],
      landedSha: null, landedBaseSha: null, landedPatchEqualsReviewedPatch: null,
    });
    expect(fixture.componentInstance.loadError()).toBe(false);
    http.verify();
  });
});

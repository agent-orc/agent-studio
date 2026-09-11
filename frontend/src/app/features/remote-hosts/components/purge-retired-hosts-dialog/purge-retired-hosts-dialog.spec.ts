import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PurgeRetiredHostsDialogComponent } from './purge-retired-hosts-dialog';

function setup() {
  TestBed.configureTestingModule({
    imports: [PurgeRetiredHostsDialogComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(PurgeRetiredHostsDialogComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  return { fixture, http };
}

describe('PurgeRetiredHostsDialogComponent', () => {
  it('defaults the prefix to "e2e-" (the leftover-runner convention)', () => {
    const { fixture } = setup();
    expect(fixture.componentInstance.prefix()).toBe('e2e-');
  });

  it('previews eligible and blocked candidates for a prefix, then applies and reports the count', () => {
    const { fixture, http } = setup();
    const component = fixture.componentInstance;

    component.preview();
    const previewReq = http.expectOne('/api/clients/retired/purge');
    expect(previewReq.request.method).toBe('POST');
    expect(previewReq.request.body).toEqual({ prefix: 'e2e-', dryRun: true });
    previewReq.flush({
      dryRun: true,
      deletedCount: 0,
      candidates: [
        { id: 'e2e-a', displayName: 'e2e-a', eligible: true, refusalReason: null, deleted: false },
        { id: 'e2e-b', displayName: 'e2e-b', eligible: false, refusalReason: 'client-online', deleted: false },
      ],
    });

    expect(component.phase()).toBe('preview');
    expect(component.eligibleCount()).toBe(1);
    expect(component.candidates()).toHaveLength(2);

    component.confirmPurge();
    const applyReq = http.expectOne('/api/clients/retired/purge');
    expect(applyReq.request.body).toEqual({ prefix: 'e2e-', dryRun: false });
    applyReq.flush({
      dryRun: false,
      deletedCount: 1,
      candidates: [
        { id: 'e2e-a', displayName: 'e2e-a', eligible: true, refusalReason: null, deleted: true },
        { id: 'e2e-b', displayName: 'e2e-b', eligible: false, refusalReason: 'client-online', deleted: false },
      ],
    });

    expect(component.phase()).toBe('report');
    expect(component.deletedCount()).toBe(1);
    expect(component.blockedCandidates().map(c => c.id)).toEqual(['e2e-b']);
    http.verify();
  });

  it('previews with a custom prefix typed into the form', () => {
    const { fixture, http } = setup();
    const component = fixture.componentInstance;

    component.setPrefix('e2e-owner-');
    component.preview();
    const request = http.expectOne('/api/clients/retired/purge');
    expect(request.request.body).toEqual({ prefix: 'e2e-owner-', dryRun: true });
    request.flush({ dryRun: true, deletedCount: 0, candidates: [] });
    expect(component.phase()).toBe('preview');
    http.verify();
  });

  it('surfaces a failed preview as the error phase without changing the candidate list', () => {
    const { fixture, http } = setup();
    fixture.componentInstance.preview();
    http.expectOne('/api/clients/retired/purge').flush(
      { error: 'boom' },
      { status: 500, statusText: 'Server Error' },
    );

    expect(fixture.componentInstance.phase()).toBe('error');
    expect(fixture.componentInstance.errorText()).toBeTruthy();
    http.verify();
  });

  it('backToForm() returns to the form phase from preview or error', () => {
    const { fixture, http } = setup();
    const component = fixture.componentInstance;
    component.preview();
    http.expectOne('/api/clients/retired/purge').flush({ dryRun: true, deletedCount: 0, candidates: [] });
    expect(component.phase()).toBe('preview');

    component.backToForm();
    expect(component.phase()).toBe('form');
    http.verify();
  });
});

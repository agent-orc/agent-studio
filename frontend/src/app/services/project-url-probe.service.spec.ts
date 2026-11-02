import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { ProjectUrlProbeService, projectUrlStatusLabel } from './project-url-probe.service';

describe('ProjectUrlProbeService', () => {
  it('maps a local-only readiness to the local-only status instead of offline', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const probe = TestBed.inject(ProjectUrlProbeService);
    const http = TestBed.inject(HttpTestingController);

    expect(probe.statusFor('PROJ-1', 'url-1')).toBe('unknown');
    http.expectOne('/api/projects/PROJ-1/urls/url-1/readiness').flush({
      kind: 'local-only', statusCode: null, framePolicy: 'unknown',
      detail: 'This URL points at a loopback or link-local address.', durationMs: 0.1,
    });

    expect(probe.statusFor('PROJ-1', 'url-1')).toBe('local-only');
    http.verify();
  });

  it('labels local-only as "local only" and keeps other statuses', () => {
    expect(projectUrlStatusLabel('local-only')).toBe('local only');
    expect(projectUrlStatusLabel('offline')).toBe('offline');
  });
});

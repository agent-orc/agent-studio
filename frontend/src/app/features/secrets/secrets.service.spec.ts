import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SecretsService, secretScopeRoute, type SecretMetadata } from './secrets.service';

function metadata(scope: string, name: string, version: number): SecretMetadata {
  return {
    reference: `secret://${scope}/${name}`, scope, scopeKind: 'project', name, status: 'active',
    currentVersion: version, createdAt: '2026-10-19T09:00:00Z', createdBy: 'human:first.owner',
    updatedAt: '2026-10-19T09:00:00Z', updatedBy: 'human:first.owner', rotatedAt: null, rotatedBy: null,
    lastUsedAt: null, lastUsedBy: null, lastUsedPurpose: null, versions: [],
  };
}

describe('SecretsService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  it('maps each scope to its route group', () => {
    expect(secretScopeRoute('workspace')).toBe('/api/secrets/workspace');
    expect(secretScopeRoute('memo wikis')).toBe('/api/projects/memo%20wikis/secrets');
    expect(secretScopeRoute('memowikis/stage')).toBe('/api/projects/memowikis/environments/stage/secrets');
  });

  it('sends values only in write requests and merges the returned metadata', () => {
    const service = TestBed.inject(SecretsService);
    const http = TestBed.inject(HttpTestingController);
    service.load();
    http.expectOne('/api/secrets').flush({ store: 'builtin', keySource: 'key-file', canWriteWorkspace: true, secrets: [], unreadable: [] });

    const value = `value-${crypto.randomUUID()}`;
    service.add('memowikis/stage', 'DATABASE_URL', value).subscribe();
    const put = http.expectOne('/api/projects/memowikis/environments/stage/secrets');
    expect(put.request.method).toBe('POST');
    expect(put.request.body).toEqual({ name: 'DATABASE_URL', value });
    put.flush(metadata('memowikis/stage', 'DATABASE_URL', 1));

    service.rotate('memowikis/stage', 'DATABASE_URL', value).subscribe();
    http.expectOne('/api/projects/memowikis/environments/stage/secrets/DATABASE_URL/rotate')
      .flush(metadata('memowikis/stage', 'DATABASE_URL', 2));
    service.revoke('memowikis/stage', 'DATABASE_URL', 1).subscribe();
    const revoke = http.expectOne('/api/projects/memowikis/environments/stage/secrets/DATABASE_URL/revoke');
    expect(revoke.request.body).toEqual({ version: 1 });
    revoke.flush(metadata('memowikis/stage', 'DATABASE_URL', 2));

    expect(service.overview()?.secrets.map(item => item.currentVersion)).toEqual([2]);
    expect(JSON.stringify(service.overview())).not.toContain(value);
    http.verify();
  });

  it('surfaces the server message when a write is refused', () => {
    const service = TestBed.inject(SecretsService);
    const http = TestBed.inject(HttpTestingController);
    service.add('workspace', 'MAIL_KEY', 'x').subscribe({ error: () => undefined });
    http.expectOne('/api/secrets/workspace').flush(
      { error: 'project-scope-denied', message: 'Workspace secrets need access to every project.' },
      { status: 403, statusText: 'Forbidden' });
    expect(service.error()).toBe('Workspace secrets need access to every project.');
  });
});

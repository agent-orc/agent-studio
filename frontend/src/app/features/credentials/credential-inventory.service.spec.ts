import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CredentialInventoryService } from './credential-inventory.service';
import { AuthSessionState } from '../../services/auth.service';

describe('CredentialInventoryService', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('refreshes board and activity alerts while the session stays open', () => {
    TestBed.inject(AuthSessionState).status.set({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
    });
    const service = TestBed.inject(CredentialInventoryService);
    const http = TestBed.inject(HttpTestingController);
    const url = '/api/v1/management/credential-inventory';
    service.ensureLoaded();
    http.expectOne(url).flush({ generatedAt: 'first', items: [] });
    service.ensureLoaded();
    vi.advanceTimersByTime(60_000);
    http.expectOne(url).flush({ generatedAt: 'second', items: [
      { id: 'gap', state: 'missing' },
    ] });
    expect(service.inventory()?.items[0].id).toBe('gap');
    vi.advanceTimersByTime(60_000);
    http.expectOne(url).flush({ generatedAt: 'third', items: [] });
    expect(service.inventory()?.items).toHaveLength(0);
    http.verify();
  });

  it('does not request Task Server inventory in the local profile', () => {
    TestBed.inject(AuthSessionState).status.set({
      profile: 'local', bootstrapRequired: false, authenticated: false,
    });
    const service = TestBed.inject(CredentialInventoryService);
    const http = TestBed.inject(HttpTestingController);
    service.ensureLoaded();
    service.load();
    vi.advanceTimersByTime(60_000);
    http.expectNone('/api/v1/management/credential-inventory');
    expect(service.state()).toBe('unsupported');
    http.verify();
  });

  it('waits for auth startup before deciding whether the inventory route exists', () => {
    const auth = TestBed.inject(AuthSessionState);
    const service = TestBed.inject(CredentialInventoryService);
    const http = TestBed.inject(HttpTestingController);
    service.ensureLoaded();
    http.expectNone('/api/v1/management/credential-inventory');
    auth.status.set({ profile: 'local', bootstrapRequired: false, authenticated: false });
    TestBed.tick();
    http.expectNone('/api/v1/management/credential-inventory');
    expect(service.state()).toBe('unsupported');
    http.verify();
  });

  it('loads once when auth startup identifies a networked profile', () => {
    const auth = TestBed.inject(AuthSessionState);
    const service = TestBed.inject(CredentialInventoryService);
    const http = TestBed.inject(HttpTestingController);
    service.ensureLoaded();
    http.expectNone('/api/v1/management/credential-inventory');
    auth.status.set({ profile: 'networked', bootstrapRequired: false, authenticated: true });
    TestBed.tick();
    http.expectOne('/api/v1/management/credential-inventory').flush({ generatedAt: 'now', items: [] });
    expect(service.state()).toBe('ready');
    http.verify();
  });
});

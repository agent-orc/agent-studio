import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resolveUpdateServiceUrl, UPDATE_TOKEN_HEADER, UPDATE_TOKEN_ROUTE, UpdateClientService } from './update.service';

describe('resolveUpdateServiceUrl', () => {
  it('skips machine-local polling on an HTTPS website without a configured service', () => {
    expect(resolveUpdateServiceUrl('https://studio.example/board')).toBeNull();
  });

  it('does not poll an optional service on a local installation without configuration', () => {
    expect(resolveUpdateServiceUrl('http://localhost:4010/board')).toBeNull();
  });

  it('uses an explicit service endpoint on HTTPS', () => {
    expect(resolveUpdateServiceUrl('https://studio.example/', 'https://updates.example'))
      .toBe('https://updates.example');
  });
});

describe('UpdateClientService trigger token', () => {
  let http: HttpTestingController;
  let client: UpdateClientService;
  const serviceWindow = window as Window & { __AGENT_STUDIO_UPDATE_SERVICE_URL__?: string };

  beforeEach(() => {
    // Keep the status poll from firing; these cases drive the mutations only.
    vi.useFakeTimers();
    serviceWindow.__AGENT_STUDIO_UPDATE_SERVICE_URL__ = 'http://localhost:5039';
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(UpdateClientService);
  });

  afterEach(() => {
    delete serviceWindow.__AGENT_STUDIO_UPDATE_SERVICE_URL__;
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  // A generated fixture value, never a real token.
  const fixtureToken = (label: string) => `fixture-${label}-${Math.random().toString(36).slice(2)}`;

  async function next(predicate: (request: TestRequest) => boolean): Promise<TestRequest> {
    for (let attempt = 0; attempt < 20; attempt++) {
      const found = http.match(() => true).find(predicate);
      if (found) return found;
      await Promise.resolve();
      await vi.advanceTimersByTimeAsync(0);
    }
    throw new Error('expected request was not sent');
  }

  const isTokenRead = (request: TestRequest) => request.request.url === UPDATE_TOKEN_ROUTE;
  const isPost = (path: string) => (request: TestRequest) =>
    request.request.method === 'POST' && request.request.url.endsWith(path);

  it('sends the token the Studio backend reads from the token file on trigger', async () => {
    const token = fixtureToken('trigger');
    const pending = client.trigger('manual', true);

    (await next(isTokenRead)).flush({ token });
    const trigger = await next(isPost('/update/trigger'));
    expect(trigger.request.headers.get(UPDATE_TOKEN_HEADER)).toBe(token);
    trigger.flush({ runId: 'run-1', phase: 'preparing', message: 'accepted' });

    await expect(pending).resolves.toMatchObject({ runId: 'run-1' });
  });

  it('refetches the token once when the service refuses it', async () => {
    const stale = fixtureToken('stale');
    const fresh = fixtureToken('fresh');
    const pending = client.rollback('run-7');

    (await next(isTokenRead)).flush({ token: stale });
    const first = await next(isPost('/update/rollback'));
    expect(first.request.headers.get(UPDATE_TOKEN_HEADER)).toBe(stale);
    first.flush({ error: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    (await next(isTokenRead)).flush({ token: fresh });
    const retry = await next(isPost('/update/rollback'));
    expect(retry.request.headers.get(UPDATE_TOKEN_HEADER)).toBe(fresh);
    retry.flush({ runId: 'run-7', phase: 'rolling-back', message: 'accepted' });

    await expect(pending).resolves.toMatchObject({ phase: 'rolling-back' });
  });
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  GITHUB_APP_NAVIGATOR,
  GitHubAppService,
  readGitHubAppReturn,
  type GitHubAppFlowStep,
  type GitHubAppStatus,
} from './github-app.service';

const root = '/api/v1/management/github-app';

const disconnected: GitHubAppStatus = {
  connected: false,
  appId: null,
  slug: null,
  name: null,
  ownerLogin: null,
  htmlUrl: null,
  installUrl: null,
  connectedAtUtc: null,
  installations: [],
  repositories: [],
  hosts: [],
  problem: null,
};

describe('GitHubAppService', () => {
  let service: GitHubAppService;
  let http: HttpTestingController;
  let navigator: { assign: ReturnType<typeof vi.fn>; postForm: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    navigator = { assign: vi.fn(), postForm: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GITHUB_APP_NAVIGATOR, useValue: navigator },
      ],
    });
    service = TestBed.inject(GitHubAppService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts the manifest form to GitHub, not through Studio', () => {
    service.startRegistration(' agent-orc ', true);
    const request = http.expectOne(`${root}/registration`);
    expect(request.request.body).toEqual({ origin: window.location.origin, organization: 'agent-orc', anyAccount: true });
    request.flush({ actionUrl: 'https://github.com/settings/apps/new?state=s', manifest: '{"name":"x"}', state: 's' });
    expect(navigator.postForm).toHaveBeenCalledExactlyOnceWith('https://github.com/settings/apps/new?state=s', { manifest: '{"name":"x"}' });
  });

  it('sends an empty organization as none', () => {
    service.startRegistration('   ', false);
    const request = http.expectOne(`${root}/registration`);
    expect(request.request.body.organization).toBeNull();
    expect(request.request.body.anyAccount).toBe(false);
    request.flush({ actionUrl: 'https://github.com/x', manifest: '{}', state: 's' });
  });

  it('resumes a captured return once and follows the next step', () => {
    const history = { replaceState: vi.fn() };
    expect(service.captureReturn(
      { pathname: '/github-app/callback', search: '?state=abc&installation_id=42&setup_action=install' }, history)).toBe(true);

    expect(service.resumePending()).toBe(true);
    const request = http.expectOne(`${root}/flow`);
    expect(request.request.body).toEqual({
      origin: window.location.origin, state: 'abc', code: null, installationId: 42, setupAction: 'install',
    });
    const step: GitHubAppFlowStep = { nextUrl: 'https://github.com/login/oauth/authorize?x', message: 'Next', status: disconnected };
    request.flush(step);
    expect(navigator.assign).toHaveBeenCalledExactlyOnceWith('https://github.com/login/oauth/authorize?x');
    expect(service.message()).toBe('Next');

    expect(service.resumePending()).toBe(false);
  });

  it('shows the server message when a step fails', () => {
    service.startInstallation();
    http.expectOne(`${root}/installation`).flush(
      { error: 'not-connected', message: 'No GitHub App is connected.' }, { status: 409, statusText: 'Conflict' });
    expect(service.error()).toBe('No GitHub App is connected.');
    expect(service.busy()).toBe(false);
    expect(navigator.assign).not.toHaveBeenCalled();
  });

  it('reloads the status after disconnecting', () => {
    service.disconnect();
    http.expectOne(root).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(root).flush(disconnected);
    expect(service.status()?.connected).toBe(false);
  });
});

describe('readGitHubAppReturn', () => {
  it('ignores every other page', () => {
    const history = { replaceState: vi.fn() };
    expect(readGitHubAppReturn({ pathname: '/', search: '?code=x' }, history)).toBeNull();
    expect(history.replaceState).not.toHaveBeenCalled();
  });

  it('takes the single-use code out of the address bar', () => {
    const history = { replaceState: vi.fn() };
    const found = readGitHubAppReturn({ pathname: '/github-app/callback', search: '?code=c&state=s' }, history);
    expect(found).toEqual({ state: 's', code: 'c', installationId: null, setupAction: null });
    expect(history.replaceState).toHaveBeenCalledExactlyOnceWith(null, '', '/#/workspace/settings/github');
  });

  it('refuses a malformed installation id', () => {
    const history = { replaceState: vi.fn() };
    const found = readGitHubAppReturn({ pathname: '/github-app/callback', search: '?installation_id=1;drop' }, history);
    expect(found?.installationId).toBeNull();
  });
});

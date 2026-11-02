import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { GITHUB_APP_NAVIGATOR, type GitHubAppCoverageState, type GitHubAppStatus } from '../github-app.service';
import { GitHubAppPanelComponent } from './github-app-panel.component';

const root = '/api/v1/management/github-app';

const connected: GitHubAppStatus = {
  connected: true,
  appId: 7,
  slug: 'agent-studio-x',
  name: 'agent-studio-x',
  ownerLogin: 'owner',
  htmlUrl: 'https://github.com/apps/agent-studio-x',
  installUrl: 'https://github.com/apps/agent-studio-x/installations/new',
  connectedAtUtc: '2026-11-01T10:00:00Z',
  installations: [{ id: 1, account: 'owner', accountType: 'User', adoptedAtUtc: '2026-11-01T10:01:00Z', adoptedBy: 'local-operator' }],
  repositories: [
    { project: 'Covered', repositoryUrl: 'https://github.com/owner/a.git', state: 'covered', installationId: 1, detail: null, visibility: 'private' },
    { project: 'Missing', repositoryUrl: 'https://github.com/owner/b.git', state: 'not-installed', installationId: null, detail: 'Not installed.' },
    { project: 'Local', repositoryUrl: null, state: 'no-repository-url', installationId: null, detail: null },
  ],
  hosts: [{ runnerId: 'runner-1', access: 'write', repositories: 1, lastSyncUtc: '2026-11-01T10:02:00Z' }],
  problem: null,
};

describe('GitHubAppPanelComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [GitHubAppPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GITHUB_APP_NAVIGATOR, useValue: { assign: () => undefined, postForm: () => undefined } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function render(status: GitHubAppStatus): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(GitHubAppPanelComponent);
    http.expectOne(root).flush(status);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  it('offers the browser flow while nothing is connected', async () => {
    const element = await render({ ...connected, connected: false, installations: [], hosts: [] });
    expect(element.querySelector('[data-testid="github-app-connect"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="github-app-connected"]')).toBeNull();
    expect(element.querySelector('[data-testid="github-app-no-hosts"]')).not.toBeNull();
  });

  it('lists what the connection reaches and who fetched', async () => {
    const element = await render(connected);
    expect(element.querySelector('[data-testid="github-app-connected"]')?.textContent).toContain('agent-studio-x');
    expect(element.querySelector('[data-testid="github-app-installations"]')?.textContent).toContain('owner');
    const rows = element.querySelectorAll('[data-testid="github-app-repositories"] tbody tr');
    expect(rows.length).toBe(3);
    expect(rows[1].textContent).toContain('App not installed');
    const visibility = [...element.querySelectorAll('[data-testid="github-app-repository-visibility"]')].map(cell => cell.textContent?.trim());
    expect(visibility).toEqual(['Private', '-', '-']);
    expect(element.querySelector('[data-testid="github-app-uncovered"]')?.textContent).toContain('1 still use');
    expect(element.querySelector('[data-testid="github-app-hosts"]')?.textContent).toContain('runner-1');
  });

  it('shows a tone icon in front of every repository access state', async () => {
    const states: { state: GitHubAppCoverageState; tone: string; icon: string; label: string }[] = [
      { state: 'covered', tone: 'success', icon: 'check', label: 'Connected' },
      { state: 'not-installed', tone: 'warning', icon: 'warn', label: 'App not installed' },
      { state: 'not-adopted', tone: 'warning', icon: 'warn', label: 'Needs confirmation' },
      { state: 'no-repository-url', tone: 'neutral', icon: 'circle', label: 'No repository' },
      { state: 'not-github', tone: 'neutral', icon: 'circle', label: 'Not on GitHub' },
      { state: 'unknown', tone: 'neutral', icon: 'circle', label: 'Unknown' },
    ];
    const element = await render({
      ...connected,
      repositories: states.map(({ state }) => ({
        project: state, repositoryUrl: null, state, installationId: null, detail: state === 'not-adopted' ? 'Confirm on GitHub.' : null,
      })),
    });

    for (const expected of states) {
      const indicator = element.querySelector(`[data-testid="github-app-coverage-${expected.state}"]`) as HTMLElement;
      expect(indicator, expected.state).not.toBeNull();
      expect(indicator.dataset['tone'], expected.state).toBe(expected.tone);
      expect(indicator.dataset['icon'], expected.state).toBe(expected.icon);
      expect(indicator.querySelector('.status-indicator__icon svg'), expected.state).not.toBeNull();
      expect(indicator.querySelector('.status-indicator__icon')?.getAttribute('aria-hidden')).toBe('true');
      expect(indicator.textContent?.trim()).toBe(expected.label);
    }
    expect(element.querySelector('[data-testid="github-app-coverage-not-adopted"]')?.getAttribute('tabindex')).toBe('0');
    expect(element.querySelector('[data-testid="github-app-coverage-covered"]')?.getAttribute('tabindex')).toBeNull();
  });

  it('marks the connection and each confirmed installation as success', async () => {
    const element = await render(connected);
    const connection = element.querySelector('[data-testid="github-app-connection-status"]') as HTMLElement;
    expect(connection.dataset['tone']).toBe('success');
    expect(connection.textContent?.trim()).toBe('Connected');
    const installation = element.querySelector('[data-testid="github-app-installation-1"]') as HTMLElement;
    expect(installation.dataset['icon']).toBe('check');
    expect(installation.classList.contains('status-indicator--compact')).toBe(true);
  });

  it('asks once more before disconnecting', async () => {
    const fixture = TestBed.createComponent(GitHubAppPanelComponent);
    http.expectOne(root).flush(connected);
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    (element.querySelector('[data-testid="github-app-disconnect"]') as HTMLButtonElement).click();
    await settle(fixture);
    http.expectNone(root);

    (element.querySelector('[data-testid="github-app-disconnect-confirm"]') as HTMLButtonElement).click();
    const request = http.expectOne(root);
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(root).flush({ ...connected, connected: false });
  });

  it('clears the write-only fields once they are sent', () => {
    const fixture = TestBed.createComponent(GitHubAppPanelComponent);
    http.expectOne(root).flush({ ...connected, connected: false });
    const component = fixture.componentInstance;
    component.appId.set('7');
    component.clientId.set('Iv1.client');
    component.clientSecret.set('value');
    component.privateKey.set('pem');

    component.connectExisting();

    expect(component.clientSecret()).toBe('');
    expect(component.privateKey()).toBe('');
    const request = http.expectOne(`${root}/credentials`);
    expect(request.request.body.appId).toBe(7);
    request.flush(connected);
  });
});

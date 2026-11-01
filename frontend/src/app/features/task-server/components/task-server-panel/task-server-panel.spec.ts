import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskServerPanelComponent } from './task-server-panel';
import { AuthSessionState } from '../../../../services/auth.service';

/**
 * Render-path test: the panel loads live management status and renders the
 * connection / store / evidence blocks, the Runner registry, and the management
 * panel. The summary count reconciles to the visible Runner rows (R3), and a
 * command response produces a result row.
 */
describe('TaskServerPanelComponent', () => {
  it('shows querying separately from unavailable and keeps connection visible while statistics are pending', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskServerPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskServerPanelComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="task-server-querying"]')?.textContent).toContain('Querying the Task Server');
    expect(el.querySelector('[data-testid="task-server-unavailable"]')).toBeNull();

    TestBed.inject(HttpTestingController).expectOne('/api/v1/management/status').flush({
      server: { id: 'ts-1', url: 'http://localhost:4010', version: '1.0', protocolMinimum: '1.0', protocolMaximum: '1.0', uptimeSeconds: 60 },
      health: { state: 'healthy', ready: true },
      statistics: { state: 'pending', refreshedAt: null },
      store: { sizeBytes: 0, projectCount: 0, taskCount: 0, archivedTaskCount: 0, eventCount: null, artifactCount: 0, identityCount: 1 },
      evidence: { state: 'pending', eventFiles: 0, artifactFiles: 0, lastWriteAt: null },
      maintenance: { mode: 'normal', drainRequested: false, shutdownPrepared: false, reason: null }, migrations: [],
      runners: [], backups: { directory: '/tmp/backups', retentionCount: 7, lastFailure: null, items: [] },
      security: { available: true, userCount: 1, credentialRunnerCount: 0, sessionUrl: '/api/auth/session', usersUrl: '/api/auth/users', runnerCredentialsUrl: '/api/auth/runners', integration: 'shared' },
    });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="task-server-querying"]')).toBeNull();
    expect(el.querySelector('[data-testid="task-server-connection"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-statistics-pending"]')?.textContent).toContain('being computed');
    expect(el.querySelector('[data-testid="task-server-store-size"]')?.textContent).toContain('Calculating');
    expect(el.querySelector('[data-testid="task-server-backups"]')?.textContent).toContain('Calculating');
    expect(el.querySelector('[data-testid="task-server-unavailable"]')).toBeNull();
    fixture.destroy();
  });

  async function mount() {
    await TestBed.configureTestingModule({
      imports: [TaskServerPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskServerPanelComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/management/status').flush({
      server: { id: 'ts-1', url: 'http://localhost:4010', version: '1.0', protocolMinimum: '1.0', protocolMaximum: '1.0', uptimeSeconds: 60 },
      health: { state: 'healthy', ready: true },
      statistics: { state: 'ready', refreshedAt: '2026-07-20T10:00:00Z' },
      store: { sizeBytes: 1, projectCount: 1, taskCount: 2, archivedTaskCount: 0, eventCount: 3, artifactCount: 4, identityCount: 2 },
      evidence: { state: 'available', eventFiles: 1, artifactFiles: 1, lastWriteAt: null },
      maintenance: { mode: 'normal', drainRequested: false, shutdownPrepared: false, reason: null }, migrations: [],
      runners: [
        { id: 'r1', displayName: 'R1', state: 'running', lastUsedAt: null, activeSlots: 0, drainRequested: false, retireRequested: false },
        { id: 'r2', displayName: 'R2', state: 'running', lastUsedAt: null, activeSlots: 1, drainRequested: false, retireRequested: false },
      ], backups: { directory: '/tmp/backups', retentionCount: 7, lastFailure: null, items: [] },
      security: { available: true, userCount: 1, credentialRunnerCount: 2, sessionUrl: '/api/auth/session', usersUrl: '/api/auth/users', runnerCredentialsUrl: '/api/auth/runners', integration: 'shared' },
    });
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  async function mountUnavailableNetworked() {
    await TestBed.configureTestingModule({
      imports: [TaskServerPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const auth = TestBed.inject(AuthSessionState);
    auth.status.set({
      profile: 'networked',
      bootstrapRequired: false,
      authenticated: true,
      user: {
        id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false,
      },
    });
    const fixture = TestBed.createComponent(TaskServerPanelComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/management/status').flush(
      {
        error: 'authentication-required',
        message: 'Sign in with an owner or operator account to manage the Task Server.',
        loginUrl: '/api/auth/login',
      },
      { status: 401, statusText: 'Unauthorized' },
    );
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, auth };
  }

  it('mounts, loads the status, and renders every block', async () => {
    const fixture = await mount();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="task-server-panel"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-connection"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-store"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-evidence"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-statistics-age"]')?.textContent).toContain('Statistics refreshed');
    expect(el.querySelector('[data-testid="task-server-management"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-evidence"]')?.textContent).toContain('Event files');
    expect(el.querySelector('[data-testid="task-server-evidence"]')?.textContent).not.toContain('Branch');

    // The connected URL is reported by the Task Server.
    expect(el.querySelector('[data-testid="task-server-url"]')?.textContent)
      .toContain('http://localhost:4010');
    expect(el.querySelector('[data-testid="task-server-store"] .ts__mono')?.hasAttribute('title'))
      .toBe(false);

    fixture.destroy();
  });

  it('summary client count reconciles to the visible client rows (R3)', async () => {
    const fixture = await mount();
    const el: HTMLElement = fixture.nativeElement;

    const rows = el.querySelectorAll('[data-testid="task-server-clients"] > li');
    expect(rows.length).toBeGreaterThanOrEqual(2);

    const summary = el.querySelector('[data-testid="task-server-summary"]')?.textContent ?? '';
    expect(summary).toContain(String(rows.length));

    const sectionCount = el.querySelector('[data-testid="task-server-clients-section"] .ts__section-count')?.textContent ?? '';
    expect(sectionCount).toContain(String(rows.length));

    fixture.destroy();
  });

  it('running a sweep records a result row', async () => {
    const fixture = await mount();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="task-server-results-empty"]')).toBeTruthy();

    const btn = el.querySelector('[data-testid="task-server-action-archive-sweep"]') as HTMLButtonElement;
    btn.click();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/management/commands').flush({
      commandId: 'cmd_1', kind: 'archive-sweep', dryRun: true, state: 'completed', matched: 2, affected: 0,
      summary: '2 tasks would be archived.', completedAt: '2026-07-20T00:00:00Z',
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="task-server-result-archive-sweep"]')).toBeTruthy();

    fixture.destroy();
  });

  it('renders the networked authentication reason and a sign-in entry', async () => {
    const { fixture, auth } = await mountUnavailableNetworked();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="task-server-unavailable"]')?.textContent)
      .toContain('Sign in with an owner or operator account');
    const signIn = el.querySelector('[data-testid="task-server-sign-in"]') as HTMLButtonElement;
    expect(signIn).toBeTruthy();

    signIn.click();
    expect(auth.studioAllowed()).toBe(false);

    fixture.destroy();
  });
});

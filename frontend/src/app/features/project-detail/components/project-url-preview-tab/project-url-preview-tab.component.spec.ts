import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ProjectUrlPreviewTabComponent } from './project-url-preview-tab.component';
import {
  ProjectUrlProbeService,
  type ProjectUrlReadiness,
  type ProjectUrlStatus,
} from '../../../../services/project-url-probe.service';
import type {
  ProjectUrlDiagnostic,
  ProjectUrlProcessSnapshot,
  ProjectUrlSuggestion,
  RegistryWorkspaceListItem,
  RegistryProjectUrl,
} from '../../../../models/task.model';
import { ProjectUrlRecoveryService } from '../../services/project-url-recovery.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { AuthSessionState } from '../../../../services/auth.service';

/** Signal-backed probe stub so tests drive running/offline without a real fetch. */
class ProbeStub {
  readonly status = signal<ProjectUrlStatus>('unknown');
  readonly readiness = computed<ProjectUrlReadiness>(() => {
    const status = this.status();
    if (status === 'running') return { kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 1 };
    if (status === 'failed') return { kind: 'http-error', statusCode: 500, framePolicy: 'allowed', detail: null, durationMs: 1 };
    if (status === 'blocked') return { kind: 'frame-blocked', statusCode: 200, framePolicy: 'blocked', detail: 'X-Frame-Options is DENY.', durationMs: 1 };
    if (status === 'offline') return { kind: 'offline', statusCode: null, framePolicy: 'unknown', detail: null, durationMs: 1 };
    return { kind: 'unknown', statusCode: null, framePolicy: 'unknown', detail: null, durationMs: null };
  });
  statusFor(): ProjectUrlStatus { return this.status(); }
  readinessFor(): ProjectUrlReadiness { return this.readiness(); }
  signalFor() { return this.readiness; }
  refresh(): void { /* no-op */ }
}

function workspacesWith(urls: RegistryProjectUrl[]): RegistryWorkspaceListItem[] {
  return [{
    id: 'ws-1', displayName: 'WS', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-01-01T00:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM', workspaceId: 'ws-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: 'c:/tasks', repositoryPath: 'c:/demo', rootPath: null, repositoryUrl: null,
      urls, archived: false, createdAt: '2026-01-01T00:00:00Z',
    }],
  }];
}

function mount(urlId = 'url-1', profile: 'local' | 'networked' = 'local') {
  const probe = new ProbeStub();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: ProjectUrlProbeService, useValue: probe },
      { provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } },
    ],
  });
  TestBed.inject(AuthSessionState).status.set({
    profile, bootstrapRequired: false, authenticated: true,
  });
  const fixture = TestBed.createComponent(ProjectUrlPreviewTabComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('urlId', urlId);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  return { fixture, http, probe };
}

const RUNNING_URL: RegistryProjectUrl = {
  id: 'url-1', label: 'Lab', url: 'http://localhost:4201', sortOrder: 0, startRule: null,
};
const STARTABLE_URL: RegistryProjectUrl = {
  id: 'url-1', label: 'Website', url: 'http://localhost:4202', sortOrder: 0,
  startRule: { command: 'npm run website', cwd: null, port: null, source: 'manual' },
};

function processSnapshot(
  state: ProjectUrlProcessSnapshot['state'] = 'running',
  output = ['ready in 412 ms'],
): ProjectUrlProcessSnapshot {
  return {
    started: true,
    projectId: 'PROJ-001',
    urlId: 'url-1',
    command: 'npm run website',
    cwd: 'c:/demo',
    state,
    processId: 42,
    startedAtUtc: '2026-07-13T20:00:00Z',
    finishedAtUtc: state === 'running' ? null : '2026-07-13T20:01:00Z',
    exitCode: state === 'running' ? null : 0,
    output,
  };
}

function diagnostic(classification: ProjectUrlDiagnostic['classification']): ProjectUrlDiagnostic {
  return {
    classification, summary: 'Diagnostic summary', recommendedAction: 'Recommended action', command: 'npm run website',
    cwd: 'c:/demo', url: 'http://localhost:4202', configuredPort: 4202, processCreated: false,
    exitCode: null, stdoutTail: '', stderrTail: '', timedOut: false, portReachable: classification === 'running',
    httpStatus: classification === 'running' ? 200 : null, contentReady: classification === 'running',
    checkedAt: '2026-07-13T00:00:00Z',
  };
}

const README_SUGGESTION: ProjectUrlSuggestion = {
  label: 'Agent Studio Website', url: 'http://localhost:4184',
  command: 'npm start -- --host 127.0.0.1 --port 4184', cwd: 'c:/demo/04-angular-static-final',
  port: 4184, source: 'readme',
};

describe('ProjectUrlPreviewTabComponent', () => {
  afterEach(() => vi.useRealTimers());

  it('uses the registered HTTPS edge route in networked mode', () => {
    const { fixture, http, probe } = mount('url-1', 'networked');
    probe.status.set('running');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();
    const frame = fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]') as HTMLIFrameElement;
    expect(frame.getAttribute('src')).toBe('/api/projects/PROJ-001/urls/url-1/preview/');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect((fixture.nativeElement.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement).disabled).toBe(true);
  });

  it('mounts the sandboxed iframe once the URL resolves (running / unknown)', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('running');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const frame = el.querySelector<HTMLIFrameElement>('[data-testid="url-preview-frame"]');
    expect(frame).toBeTruthy();
    // Sandbox present, but no top-navigation escape.
    expect(frame?.getAttribute('sandbox')).toContain('allow-scripts');
    expect(frame?.getAttribute('sandbox')).not.toContain('allow-top-navigation');
    const addr = el.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement;
    expect(addr?.value).toBe('http://localhost:4201');
  });

  it('mirrors embed-reported navigations in the address bar without remounting', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('running');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    const frame = fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]') as HTMLIFrameElement;
    window.dispatchEvent(new MessageEvent('message', {
      source: frame.contentWindow,
      origin: 'http://localhost:4201',
      data: { source: 'url-preview-embed', type: 'navigation', url: 'http://localhost:4201/?path=src/Program.cs' },
    }));
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement;
    expect(input.value).toBe('http://localhost:4201/?path=src/Program.cs');
    expect(frame.getAttribute('src')).toBe('http://localhost:4201');

    // A message that does not come from the preview iframe is ignored.
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'http://localhost:4201',
      data: { source: 'url-preview-embed', type: 'navigation', url: 'http://localhost:4201/forged' },
    }));
    fixture.detectChanges();
    expect(input.value).toBe('http://localhost:4201/?path=src/Program.cs');
  });

  it('navigates the embedded frame to a typed URL without touching the registry', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement;
    input.value = 'http://localhost:4202/health';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    const frame = fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]') as HTMLIFrameElement;
    expect(frame?.getAttribute('src')).toBe('http://localhost:4202/health');
    expect(fixture.componentInstance.urlRecord()?.url).toBe('http://localhost:4202');
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-status"]')?.getAttribute('data-status'))
      .toBe('custom');
  });

  it('shows a contained offline panel with project context and a Start button', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const offline = el.querySelector('[data-testid="url-preview-offline"]');
    expect(offline).toBeTruthy();
    expect(offline?.querySelector('.url-preview__state-card')).toBeTruthy();
    expect(offline?.textContent).toContain('Demo');
    expect(offline?.textContent).toContain('Website');
    expect(offline?.textContent).toContain('npm run website');
    expect(offline?.textContent).toContain('c:/demo');
    expect(el.querySelector('[data-testid="url-preview-start"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="url-preview-frame"]')).toBeFalsy();
  });

  it('does not mount an HTTP 500 document and shows status plus recovery actions', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('failed');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const failure: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-http-error"]');
    expect(failure?.textContent).toContain('HTTP 500');
    expect(failure?.querySelector('[data-testid="url-preview-failure-reload"]')).toBeTruthy();
    expect(failure?.querySelector('[data-testid="url-preview-restart"]')).toBeTruthy();
    expect(failure?.querySelector('[data-testid="url-preview-failure-open-external"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]')).toBeFalsy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-status"]')?.getAttribute('data-status')).toBe('failed');
  });

  it('shows a removed state when the URL is no longer on the project', () => {
    const { fixture, http } = mount('url-gone');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="url-preview-not-found"]')).toBeTruthy();
  });

  it('opens settings for the current embed without leaving the preview', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    fixture.componentInstance.onSettings();
    fixture.detectChanges();

    expect(fixture.componentInstance.settingsOpen()).toBe(true);
    expect(document.querySelector('[data-testid="url-preview-settings-dialog"]')).toBeTruthy();
  });

  it('keeps the pending state visible until the started URL is reachable', () => {
    vi.useFakeTimers();
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.start();
    fixture.detectChanges();
    // While starting, the progress row is the single pending indicator; the
    // Start button leaves the action row instead of duplicating it as
    // a disabled "Starting…" state.
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-start"]')).toBeFalsy();
    const starting: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-starting"]');
    expect(starting?.textContent).toContain('Waiting for console output');
    expect(starting?.textContent).toContain('accept connections');
    expect(starting?.textContent).toContain('http://localhost:4202');
    const post = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls/url-1/start'));
    expect(post.request.method).toBe('POST');
    post.flush(processSnapshot());
    fixture.detectChanges();
    expect(fixture.componentInstance.process.session()?.output).toContain('ready in 412 ms');

    probe.status.set('running');
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-offline"]')).toBeFalsy();
  });

  it('renders backend start errors with Retry and Edit settings actions', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST').flush({
      error: 'Working directory does not exist: c:/missing',
      command: 'npm run website',
      cwd: 'c:/missing',
    }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    const failed: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-start-failed"]');
    expect(failed?.textContent).toContain('Working directory does not exist');
    expect(failed?.querySelector('[data-testid="url-preview-retry"]')).toBeTruthy();
    // Command and cwd live in the console below the card (the card drops its
    // duplicate copy while the console is visible).
    expect(failed?.querySelector('[data-testid="url-preview-start-details"]')).toBeFalsy();
    const consoleEl: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-process-console"]');
    expect(consoleEl?.textContent).toContain('npm run website');
    expect(consoleEl?.textContent).toContain('c:/missing');

    (failed?.querySelector('[data-testid="url-preview-edit-settings"]') as HTMLButtonElement).click();
    expect(fixture.componentInstance.settingsOpen()).toBe(true);
  });

  it('shows an occupied port with process name and PID in the readiness card', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();
    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic'))
      .flush(diagnostic('not-started'));
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([]);

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST').flush({
      error: 'Port 4202 is already in use by marketing-app (PID 9123).',
      command: 'npm run website',
      cwd: 'c:/demo',
      classification: 'port-in-use',
      occupyingProcessId: 9123,
      occupyingProcessName: 'marketing-app',
    }, { status: 400, statusText: 'Bad Request' });
    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic')).flush({
      ...diagnostic('port-in-use'),
      summary: 'Port 4202 is already in use by marketing-app (PID 9123).',
      recommendedAction: 'Stop the occupying process or configure a different preview port, then Retry.',
      startupFailureReason: 'port-in-use',
      occupyingProcessId: 9123,
      occupyingProcessName: 'marketing-app',
      portReachable: true,
    });
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([]);
    fixture.detectChanges();

    const failed: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-start-failed"]');
    expect(failed?.textContent).toContain('Preview port is occupied');
    expect(failed?.textContent).toContain('marketing-app (PID 9123)');
    expect(failed?.textContent).toContain('Stop the occupying process');
    expect(failed?.querySelector('[data-testid="url-preview-retry"]')).toBeTruthy();
  });

  it('keeps waiting beyond the former 20-second limit while console output stays active', () => {
    vi.useFakeTimers();
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();
    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic'))
      .flush(diagnostic('not-started'));
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST').flush(processSnapshot('starting', ['Compiling application bundles...']));
    vi.advanceTimersByTime(25_000);
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(processSnapshot('starting', ['Compiling application bundles...', 'Generating browser application bundles...']));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-start-failed"]')).toBeFalsy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-starting"]')?.textContent)
      .toContain('Console output is active');
    const consoleEl: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-process-console"]');
    expect(consoleEl?.textContent).toContain('Generating browser application bundles');
    expect(consoleEl?.querySelector('[data-testid="url-preview-process-status"]')?.textContent)
      .toContain('Starting · console active');
  });

  it.each([
    ['process-exit', 'Preview process exited'],
    ['silence-timeout', 'Preview start went quiet'],
    ['startup-limit', 'Preview reached startup limit'],
  ] as const)('shows the honest %s terminal diagnosis', (reason, heading) => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();
    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic'))
      .flush(diagnostic('not-started'));
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST').flush(processSnapshot('starting', ['Building...']));
    fixture.componentInstance.process.session.set(processSnapshot(
      reason === 'process-exit' ? 'exited' : 'failed',
      reason === 'process-exit' ? ['Process exited with code 7'] : ['Readiness failed'],
    ));
    fixture.detectChanges();

    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic')).flush({
      ...diagnostic(reason === 'process-exit' ? 'process-exited' : 'timeout'),
      startupFailureReason: reason,
      summary: `Terminal cause: ${reason}`,
      exitCode: reason === 'process-exit' ? 7 : null,
      timedOut: reason !== 'process-exit',
    });
    fixture.detectChanges();

    const state: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-offline"]');
    expect(state?.getAttribute('data-failure-reason')).toBe(reason);
    expect(state?.textContent).toContain(heading);
    expect(state?.textContent).toContain(`Terminal cause: ${reason}`);
  });

  it('shows bounded live output in place and stops the owned process explicitly', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST' && req.url.endsWith('/start'))
      .flush(processSnapshot('running', ['vite --port 4202', 'ready in 412 ms']));
    fixture.detectChanges();

    const console: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-process-console"]');
    expect(console?.textContent).toContain('npm run website');
    expect(console?.querySelector('[data-testid="url-preview-process-output"]')?.textContent)
      .toContain('ready in 412 ms');

    fixture.componentInstance.stop();
    http.expectOne(req => req.method === 'DELETE' && req.url.endsWith('/process'))
      .flush(processSnapshot('stopped', ['[studio] Process stopped by operator.']));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-process-status"]')?.textContent)
      .toContain('Stopped');
  });

  it('offers start, console, stop, settings, and external actions in the embed menu', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process')).flush(processSnapshot());
    fixture.detectChanges();

    const rows = fixture.componentInstance.menuItems().filter(item => item.kind === 'row');
    expect(rows.map(item => item.id)).toEqual(['start', 'console', 'stop', 'settings', 'external']);
    expect(rows.find(item => item.id === 'stop')?.disabled).toBe(false);
  });

  it('saves URL, command, cwd, and port from the per-embed settings dialog', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.saveSettings({
      label: 'Website',
      url: 'http://localhost:4300',
      startRule: { command: 'npm run preview', cwd: 'c:/demo/web', port: 4300, source: 'manual' },
    });
    const request = http.expectOne(req => req.method === 'PUT' && req.url.endsWith('/PROJ-001/urls/url-1'));
    expect(request.request.body).toMatchObject({
      url: 'http://localhost:4300',
      startRule: { command: 'npm run preview', cwd: 'c:/demo/web', port: 4300 },
    });
    request.flush(workspacesWith([STARTABLE_URL])[0].projects[0]);
    fixture.detectChanges();

    expect(fixture.componentInstance.urlRecord()?.url).toBe('http://localhost:4300');
    expect(fixture.componentInstance.urlRecord()?.startRule?.cwd).toBe('c:/demo/web');
  });

  // ── AGT-2180: actionable diagnostics + quick setup handoff ─────────────

  it('enriches the offline card with backend diagnosis, recommendation, and technical details', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic')).flush({
      ...diagnostic('invalid-cwd'), summary: 'The configured working directory does not exist.',
      recommendedAction: 'Open Settings and choose an existing project folder.', stderrTail: 'missing folder',
    });
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([]);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="url-preview-offline"]')?.getAttribute('data-diagnosis')).toBe('invalid-cwd');
    expect(el.textContent).toContain('The working directory is invalid');
    expect(el.querySelector('[data-testid="url-preview-recommendation"]')?.textContent)
      .toContain('Open Settings and choose an existing project folder.');
    expect(el.querySelector('[data-testid="url-preview-open-setup"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="url-preview-details"]')?.textContent).toContain('missing folder');
    expect(el.querySelector('[data-testid="url-preview-copy-diagnostics"]')).toBeTruthy();
  });

  it('applies a matching detected setup and starts it without leaving Preview', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic')).flush(diagnostic('invalid-cwd'));
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([README_SUGGESTION]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-apply-setup"]')).toBeTruthy();
    fixture.componentInstance.applyDetectedSetup();

    const put = http.expectOne(req => req.method === 'PUT' && req.url.endsWith('/PROJ-001/urls/url-1'));
    expect(put.request.body.startRule).toMatchObject({
      command: README_SUGGESTION.command, cwd: README_SUGGESTION.cwd, port: 4184, source: 'readme',
    });
    const corrected: RegistryProjectUrl = {
      ...STARTABLE_URL,
      startRule: {
        command: README_SUGGESTION.command, cwd: README_SUGGESTION.cwd, port: 4184,
        healthUrl: 'http://localhost:4184', readinessTimeoutSeconds: 30,
        startupTimeoutSeconds: 600, source: 'readme',
      },
    };
    put.flush({ ...workspacesWith([corrected])[0].projects[0] });
    fixture.detectChanges();

    http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls/url-1/start'))
      .flush(processSnapshot());
    expect(fixture.componentInstance.urlRecord()?.startRule?.source).toBe('readme');
  });

  it('hands the failing URL and detected suggestion to the Settings quick setup', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    let emitted: { projectName: string } | null = null;
    fixture.componentInstance.openSettings.subscribe(value => emitted = value);
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    http.expectOne(req => req.url.endsWith('/PROJ-001/urls/url-1/diagnostic')).flush(diagnostic('invalid-cwd'));
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([README_SUGGESTION]);
    fixture.detectChanges();

    fixture.componentInstance.openQuickSetup();
    expect(emitted).toEqual({ projectName: 'Demo' });
    expect(TestBed.inject(ProjectUrlRecoveryService).takeQuickSetupRequest()).toEqual({
      urlId: 'url-1', suggestion: README_SUGGESTION,
    });
  });
});

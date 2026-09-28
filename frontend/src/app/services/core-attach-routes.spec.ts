import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth.service';
import { ErrorDialogService } from './error-dialog.service';
import { JobsHubClient, type JobsHubHandlers } from './jobs-hub-client.service';
import { TaskService } from './task.service';
import { resolveAttachmentUrl } from '../features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.util';

/**
 * AGT-2983: the 27 P0 core-attach operations call their versioned Task Server
 * routes. Each case pins the exact method and URL a shared service sends, so a
 * call site cannot slip back onto the legacy `/api` surface unnoticed (the
 * route-inventory guard in docs/studio-route-ownership covers the same set
 * from the source side). The workbench turn is pinned in
 * workbench-decision.store.spec.ts.
 */

const EMPTY_BOARD = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
  codeNotComplete: [], autoReview: [], humanReview: [], escalated: [], review: [], completed: [], archive: [],
};

class JobsHubClientStub {
  readonly connected = signal(false);
  handlers: JobsHubHandlers | null = null;
  start(handlers: JobsHubHandlers): void { this.handlers = handlers; }
  stop(): void { return undefined; }
}

describe('core-attach versioned routes', () => {
  let http: HttpTestingController;
  let tasks: TaskService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ErrorDialogService, useValue: { show: () => undefined } },
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    tasks = TestBed.inject(TaskService);
  });

  afterEach(() => http.verify());

  function expectRequest(method: string, url: string, body: object = {}) {
    const request = http.expectOne((candidate) => candidate.url === url);
    expect(request.request.method).toBe(method);
    request.flush(body);
    return request;
  }

  it('sends the five auth operations to /api/v1/studio/auth', () => {
    const auth = TestBed.inject(AuthService);
    auth.initialize();
    expectRequest('GET', '/api/v1/studio/auth/status');
    auth.login('owner', 'secret').subscribe();
    expectRequest('POST', '/api/v1/studio/auth/login');
    auth.bootstrap('owner', 'secret', 'Owner').subscribe();
    expectRequest('POST', '/api/v1/studio/auth/bootstrap');
    auth.changePassword('old', 'new').subscribe();
    expectRequest('POST', '/api/v1/studio/auth/change-password');
    auth.logout().subscribe();
    expectRequest('POST', '/api/v1/studio/auth/logout');
  });

  it('reads the board and runner status from the Studio projections', () => {
    tasks.refresh(true);
    expectRequest('GET', '/api/v1/studio/board', EMPTY_BOARD);
    expectRequest('GET', '/api/v1/studio/runner/status', { projects: {} });
    tasks.getRunnerStatus().subscribe();
    expectRequest('GET', '/api/v1/studio/runner/status', { projects: {} });
  });

  it('lists and creates workspaces and projects on the v1 collection routes', () => {
    tasks.getRegistryWorkspaces({ includeArchived: true }).subscribe();
    expect(expectRequest('GET', '/api/v1/workspaces').request.params.get('includeArchived')).toBe('true');
    tasks.createRegistryWorkspace('Workspace').subscribe();
    expectRequest('POST', '/api/v1/workspaces');
    tasks.getRegistryProjects().subscribe();
    expectRequest('GET', '/api/v1/projects');
    tasks.createRegistryProject({ displayName: 'Project' } as Parameters<TaskService['createRegistryProject']>[0]).subscribe();
    expectRequest('POST', '/api/v1/projects');
  });

  it('addresses task detail by project name, or by the unscoped token with the watch path', () => {
    tasks.getDetailByProject('AGT-1', 'Agent Studio').subscribe();
    const byProject = expectRequest('GET', '/api/v1/projects/Agent%20Studio/tasks/AGT-1');
    expect(byProject.request.params.get('project')).toBe('Agent Studio');

    tasks.getDetail('AGT-1', 'C:/watch').subscribe();
    const byWatchPath = expectRequest('GET', '/api/v1/projects/-/tasks/AGT-1');
    expect(byWatchPath.request.params.get('watchPath')).toBe('C:/watch');
  });

  it('sends the seven task lifecycle verbs to the project-scoped v1 task routes', () => {
    const base = '/api/v1/projects/-/tasks/AGT-1';
    tasks.updateState('AGT-1', '2-ready', 'C:/watch').subscribe();
    expect(expectRequest('PUT', `${base}/state`).request.params.get('watchPath')).toBe('C:/watch');
    tasks.moveJob('AGT-1', '2-ready', 'C:/watch').subscribe();
    expectRequest('POST', `${base}/move`);
    tasks.moveJobToTop('AGT-1', 'C:/watch').subscribe();
    expectRequest('POST', `${base}/move-to-top`);
    tasks.startJob('AGT-1', 'C:/watch').subscribe();
    expectRequest('POST', `${base}/start`);
    tasks.stopJob('AGT-1', 'C:/watch', 'followup').subscribe();
    expect(expectRequest('POST', `${base}/stop`).request.params.get('reason')).toBe('followup');
    tasks.continueJob('AGT-1', 'Go on', 'C:/watch').subscribe();
    expectRequest('POST', `${base}/continue`);
    tasks.deleteJob('AGT-1', 'C:/watch').subscribe();
    expectRequest('DELETE', base);
  });

  it('reads orchestrator context digests, sessions and project chat from the Studio routes', () => {
    tasks.getOrchestratorContextSessions().subscribe();
    expectRequest('GET', '/api/v1/studio/orchestrator/sessions');
    tasks.getOrchestratorContextDigest('task:Agent Studio/AGT-1').subscribe();
    expectRequest('GET', '/api/v1/studio/orchestrator/context/task:Agent%20Studio/AGT-1');
    tasks.refreshOrchestratorContextDigest('project:Agent Studio').subscribe();
    expectRequest('POST', '/api/v1/studio/orchestrator/context/project:Agent%20Studio/refresh');
    tasks.getOrchestratorChat('Agent Studio').subscribe();
    expectRequest('GET', '/api/v1/studio/runner/Agent%20Studio/orchestrator-chat');
    tasks.sendOrchestratorChat('Agent Studio', { text: 'hello' }).subscribe();
    expectRequest('POST', '/api/v1/studio/runner/Agent%20Studio/orchestrator-chat');
    expect(resolveAttachmentUrl('Agent Studio', 'chat-attachments/a b.png'))
      .toBe('/api/v1/studio/runner/Agent%20Studio/orchestrator-chat/attachments/a%20b.png');
  });

  it('connects the live-update hub on /hubs/v1/studio', () => {
    const withUrl = vi.spyOn(HubConnectionBuilder.prototype, 'withUrl').mockImplementation(() => {
      throw new Error('stop after withUrl');
    });
    try {
      const hub = new JobsHubClient();
      expect(() => hub.start({})).toThrow('stop after withUrl');
      expect(withUrl).toHaveBeenCalledWith('/hubs/v1/studio');
    } finally {
      withUrl.mockRestore();
    }
  });
});

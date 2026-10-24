import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { WorkspaceQuotaBoardState, type WorkspaceQuotaAdmission, type WorkspaceQuotaStatus } from './workspace-quota-board-state.service';

const scope = (project: string | null, status: WorkspaceQuotaStatus, codingAllowed: boolean, message: string | null): WorkspaceQuotaAdmission =>
  ({ projectId: project ? `p-${project.toLowerCase()}` : null, project, status, message, overrideActive: false, codingAllowed, hostingAllowed: status !== 'hosting-full', limited: status !== 'ok' });
const board = (workspaceId: string, workspace: WorkspaceQuotaAdmission, projects: WorkspaceQuotaAdmission[] = []) =>
  ({ workspaceId, workspace, projects });
const NO_LIMIT = scope(null, 'ok', true, null);

describe('WorkspaceQuotaBoardState', () => {
  let state: WorkspaceQuotaBoardState;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    state = TestBed.inject(WorkspaceQuotaBoardState);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('reads workspace and project admission with one request and keeps only refusals as card messages', () => {
    state.load('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota/admission').flush(board('ws-a', scope(null, 'warning', true, 'Workspace A used 85 % of its weekly model tokens.'), [
      scope('Alpha', 'blocked', false, 'Alpha reached its weekly compute quota.'),
      scope('Beta', 'ok', true, null),
      scope('Gamma', 'warning', true, 'Gamma used 90 % of its weekly model tokens.'),
    ]));

    expect(state.workspaceQuota()?.status).toBe('warning');
    expect(state.messageFor('Alpha')).toBe('Alpha reached its weekly compute quota.');
    expect(state.messageFor('Beta')).toBeNull();
    expect(state.messageFor('Gamma')).toBeNull();
  });

  it('makes no per-project reads when no limit applies', () => {
    state.load('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota/admission').flush(board('ws-a', NO_LIMIT, [scope('Alpha', 'ok', true, null), scope('Beta', 'ok', true, null)]));
    http.expectNone(request => request.url.includes('/projects/'));

    expect(state.workspaceQuota()?.status).toBe('ok');
    expect(state.messageFor('Alpha')).toBeNull();
  });

  it('falls back to the workspace refusal for projects without their own limit', () => {
    state.load('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota/admission').flush(board('ws-a',
      scope(null, 'blocked', false, 'Workspace A reached its weekly model-token quota.'), [scope('Alpha', 'ok', true, null)]));

    expect(state.messageFor('Alpha')).toBe('Workspace A reached its weekly model-token quota.');
  });

  it('keeps full hosting slots off the cards because coding is still admitted', () => {
    const hostingFull = 'All 2 hosting slots are in use; new deploys wait until an environment stops.';
    state.load('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota/admission').flush(board('ws-a', scope(null, 'hosting-full', true, hostingFull), [scope('Alpha', 'ok', true, null)]));

    expect(state.workspaceQuota()?.status).toBe('hosting-full');
    expect(state.workspaceQuota()?.message).toBe(hostingFull);
    expect(state.messageFor('Alpha')).toBeNull();
  });

  it('drops the previous workspace at once and ignores its late answer', () => {
    state.load('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota/admission').flush(board('ws-a', NO_LIMIT, [scope('Alpha', 'blocked', false, 'Alpha reached its weekly compute quota.')]));
    expect(state.messageFor('Alpha')).toBe('Alpha reached its weekly compute quota.');

    state.load('ws-a');
    const lateA = http.expectOne('/api/workspaces/ws-a/quota/admission');
    state.load('ws-b');
    expect(state.messageFor('Alpha')).toBeNull();
    expect(state.workspaceQuota()).toBeNull();

    lateA.flush(board('ws-a', scope(null, 'blocked', false, 'Workspace A reached its weekly model-token quota.')));
    expect(state.workspaceQuota()).toBeNull();
    expect(state.messageFor('Alpha')).toBeNull();

    http.expectOne('/api/workspaces/ws-b/quota/admission').flush(board('ws-b', NO_LIMIT));
    expect(state.workspaceQuota()?.status).toBe('ok');
    expect(state.messageFor('Alpha')).toBeNull();
  });

  it('lets only the newest poll of the same workspace land', () => {
    state.load('ws-a');
    const first = http.expectOne('/api/workspaces/ws-a/quota/admission');
    state.load('ws-a');
    const second = http.expectOne('/api/workspaces/ws-a/quota/admission');

    second.flush(board('ws-a', NO_LIMIT));
    first.flush(board('ws-a', scope(null, 'blocked', false, 'Workspace A reached its weekly model-token quota.')));

    expect(state.workspaceQuota()?.status).toBe('ok');
    expect(state.messageFor('Alpha')).toBeNull();
  });

  it('keeps the same workspace messages across a refresh until the new answer lands', () => {
    state.load('ws-a');
    http.expectOne('/api/workspaces/ws-a/quota/admission').flush(board('ws-a',
      scope(null, 'blocked', false, 'Workspace A reached its weekly model-token quota.'), [scope('Alpha', 'blocked', false, 'Alpha reached its weekly compute quota.')]));
    state.load('ws-a');
    expect(state.workspaceQuota()?.status).toBe('blocked');
    expect(state.messageFor('Alpha')).toBe('Alpha reached its weekly compute quota.');

    http.expectOne('/api/workspaces/ws-a/quota/admission').flush('Unavailable', { status: 503, statusText: 'Service Unavailable' });
    expect(state.workspaceQuota()).toBeNull();
    expect(state.messageFor('Alpha')).toBeNull();
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { WorkspaceQuotaBoardState } from './workspace-quota-board-state.service';

const admission = (status: string, codingAllowed: boolean, message: string | null) =>
  ({ status, message, overrideActive: false, codingAllowed });

describe('WorkspaceQuotaBoardState', () => {
  let board: WorkspaceQuotaBoardState;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    board = TestBed.inject(WorkspaceQuotaBoardState);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('reads workspace and project admission and keeps only refusals as card messages', () => {
    board.load('ws-a', [{ id: 'p-alpha', displayName: 'Alpha' }, { id: 'p-beta', displayName: 'Beta' }]);
    http.expectOne('/api/workspaces/ws-a/quota').flush(admission('warning', true, 'Workspace A used 85 % of its weekly model tokens.'));
    http.expectOne('/api/workspaces/ws-a/projects/p-alpha/quota').flush(admission('blocked', false, 'Alpha reached its weekly compute quota.'));
    http.expectOne('/api/workspaces/ws-a/projects/p-beta/quota').flush(admission('ok', true, null));

    expect(board.workspaceQuota()?.status).toBe('warning');
    expect(board.messageFor('Alpha')).toBe('Alpha reached its weekly compute quota.');
    expect(board.messageFor('Beta')).toBeNull();
  });

  it('falls back to the workspace refusal for projects without their own limit', () => {
    board.load('ws-a', [{ id: 'p-alpha', displayName: 'Alpha' }]);
    http.expectOne('/api/workspaces/ws-a/quota').flush(admission('blocked', false, 'Workspace A reached its weekly model-token quota.'));
    http.expectOne('/api/workspaces/ws-a/projects/p-alpha/quota').flush(admission('ok', true, null));

    expect(board.messageFor('Alpha')).toBe('Workspace A reached its weekly model-token quota.');
  });

  it('drops the previous workspace at once and ignores its late answers', () => {
    board.load('ws-a', [{ id: 'p-alpha', displayName: 'Alpha' }]);
    const lateWorkspace = http.expectOne('/api/workspaces/ws-a/quota');
    http.expectOne('/api/workspaces/ws-a/projects/p-alpha/quota').flush(admission('blocked', false, 'Alpha reached its weekly compute quota.'));
    expect(board.messageFor('Alpha')).toBe('Alpha reached its weekly compute quota.');

    board.load('ws-b', []);
    expect(board.messageFor('Alpha')).toBeNull();
    lateWorkspace.flush(admission('blocked', false, 'Workspace A reached its weekly model-token quota.'));
    expect(board.workspaceQuota()).toBeNull();
    http.expectOne('/api/workspaces/ws-b/quota').flush(admission('ok', true, null));
    expect(board.workspaceQuota()?.status).toBe('ok');
    expect(board.messageFor('Alpha')).toBeNull();
  });

  it('keeps the same workspace messages across a refresh until the new answer lands', () => {
    board.load('ws-a', []);
    http.expectOne('/api/workspaces/ws-a/quota').flush(admission('blocked', false, 'Workspace A reached its weekly model-token quota.'));
    board.load('ws-a', []);
    expect(board.workspaceQuota()?.status).toBe('blocked');
    http.expectOne('/api/workspaces/ws-a/quota').flush('Unavailable', { status: 503, statusText: 'Service Unavailable' });
    expect(board.workspaceQuota()).toBeNull();
  });
});

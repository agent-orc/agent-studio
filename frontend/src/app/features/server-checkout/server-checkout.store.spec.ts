import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { AuthService, type AuthStatus } from '../../services/auth.service';
import { ProjectLookupService } from '../../services/project-lookup.service';
import { TaskService } from '../../services/task.service';
import type { RegistryWorkspaceListItem } from '../../models/task.model';
import { WorkspaceManagerService } from '../shell/state/workspace-manager.service';
import { ServerCheckoutStore } from './server-checkout.store';
import { urlProject } from './testing/server-checkout.fixtures';

function workspace(projects = [urlProject()]): RegistryWorkspaceListItem[] {
  return [{ id: 'ws-default', displayName: 'Default', projects } as unknown as RegistryWorkspaceListItem];
}

describe('ServerCheckoutStore (AGT-3809)', () => {
  const auth = signal<AuthStatus | null>({ authenticated: true, profile: 'local', bootstrapRequired: false });
  const tasks = { getRegistryWorkspaces: vi.fn() };
  const manager = { notifyRegistryChanged: vi.fn() };
  let store: ServerCheckoutStore;
  let lookup: ProjectLookupService;
  let http: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    auth.set({ authenticated: true, profile: 'local', bootstrapRequired: false });
    tasks.getRegistryWorkspaces.mockReset();
    manager.notifyRegistryChanged.mockReset();
    TestBed.configureTestingModule({ providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: { status: auth } },
      { provide: TaskService, useValue: tasks },
      { provide: WorkspaceManagerService, useValue: manager },
    ] });
    store = TestBed.inject(ServerCheckoutStore);
    lookup = TestBed.inject(ProjectLookupService);
    http = TestBed.inject(HttpTestingController);
    lookup.setWorkspaces(workspace([urlProject(), urlProject({ id: 'PROJ-1', displayName: 'Demo', repositoryPath: '/srv/demo' })]));
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('lists only the local URL project without a checkout', () => {
    expect(store.noticeProjects().map(project => project.id)).toEqual(['PROJ-2']);
    auth.set({ authenticated: true, profile: 'networked', bootstrapRequired: false });
    expect(store.noticeProjects()).toEqual([]);
  });

  it('starts the clone, polls progress, and refreshes the registry with the new path', () => {
    store.start('PROJ-2');
    expect(store.statusFor('PROJ-2')?.state).toBe('running');
    http.expectOne(request => request.method === 'POST' && request.url === '/api/projects/PROJ-2/server-checkout')
      .flush({ projectId: 'PROJ-2', state: 'running', step: 'clone', repositoryUrl: 'https://github.com/example/library.git',
        repositoryPath: null, branch: null, error: null });
    vi.advanceTimersByTime(1_000);
    http.expectOne('/api/projects/PROJ-2/server-checkout').flush({ projectId: 'PROJ-2', state: 'running', step: 'checkout',
      repositoryUrl: 'https://github.com/example/library.git', repositoryPath: null, branch: 'develop', error: null });
    expect(store.viewFor(lookup.allProjects()[0])?.detail).toContain('develop');

    tasks.getRegistryWorkspaces.mockReturnValue(of(workspace([urlProject({ repositoryPath: '/srv/checkouts/PROJ-2' })])));
    vi.advanceTimersByTime(1_000);
    http.expectOne('/api/projects/PROJ-2/server-checkout').flush({ projectId: 'PROJ-2', state: 'ready', step: 'register',
      repositoryUrl: 'https://github.com/example/library.git', repositoryPath: '/srv/checkouts/PROJ-2', branch: 'develop', error: null });

    expect(tasks.getRegistryWorkspaces).toHaveBeenCalledWith({ includeArchived: true });
    expect(manager.notifyRegistryChanged).toHaveBeenCalled();
    expect(lookup.allProjects()[0].repositoryPath).toBe('/srv/checkouts/PROJ-2');
    expect(store.viewFor(lookup.allProjects()[0])?.path).toBe('/srv/checkouts/PROJ-2');
    vi.advanceTimersByTime(5_000);
  });

  it('turns a refused start into a retryable failure with the backend reason', () => {
    store.start('PROJ-2');
    http.expectOne('/api/projects/PROJ-2/server-checkout')
      .flush({ error: 'This project already has a server checkout.' }, { status: 409, statusText: 'Conflict' });
    const view = store.viewFor(lookup.allProjects()[0])!;
    expect(view.state).toBe('failed');
    expect(view.detail).toBe('This project already has a server checkout.');
    expect(view.canStart).toBe(true);
  });
});

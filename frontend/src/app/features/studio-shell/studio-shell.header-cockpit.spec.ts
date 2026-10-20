import { EnvironmentInjector, provideZonelessChangeDetection, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { RegistryWorkspaceListItem } from '../../models/task.model';
import { WorkspaceQuotaBoardState } from '../quota';
import { UsageCockpitService } from '../usage-cockpit';
import { StudioHeaderCockpit, type StudioHeaderHost } from './studio-shell.header-cockpit';

function workspace(id: string, displayName: string, isDefault: boolean, projectIds: string[]): RegistryWorkspaceListItem {
  return {
    id, displayName, isDefault, sortOrder: 0, color: null, createdAt: '2026-10-19T00:00:00Z', // fixed-date: registry fixture, not a clock read
    projects: projectIds.map(projectId => ({ id: projectId, displayName: projectId.toUpperCase() })) as RegistryWorkspaceListItem['projects'],
  };
}

describe('StudioHeaderCockpit workspace quota', () => {
  const load = vi.fn();
  const reset = vi.fn();
  let workspaces: ReturnType<typeof signal<readonly RegistryWorkspaceListItem[]>>;
  let activeWorkspaceName: ReturnType<typeof signal<string | null>>;

  beforeEach(() => {
    vi.useFakeTimers();
    load.mockReset();
    reset.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: WorkspaceQuotaBoardState, useValue: { load, reset } },
        { provide: UsageCockpitService, useValue: { connect: vi.fn(), setWorkspace: vi.fn(), snapshot: signal(null), readFailed: signal(false) } },
      ],
    });
    workspaces = signal<readonly RegistryWorkspaceListItem[]>([]);
    activeWorkspaceName = signal<string | null>(null);
    runInInjectionContext(TestBed.inject(EnvironmentInjector), () => {
      const host = {
        registryWorkspaces: workspaces, activeWorkspaceName,
        globalSearchOpen: signal(false), environmentOpen: signal(false), pickerOpen: signal(false),
        chatToggle: { emit: vi.fn() }, openUsageSheet: { emit: vi.fn() }, openOrchFeed: { emit: vi.fn() }, openOrchSettings: { emit: vi.fn() },
        toggleTheme: () => undefined,
      } as unknown as StudioHeaderHost;
      return new StudioHeaderCockpit(host);
    });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('reads the default workspace in the all-projects view once the registry arrives', () => {
    TestBed.tick();
    expect(reset).toHaveBeenCalledTimes(1);
    expect(load).not.toHaveBeenCalled();

    workspaces.set([workspace('ws-b', 'Workspace B', false, []), workspace('ws-a', 'Workspace A', true, ['p-alpha'])]);
    TestBed.tick();
    expect(load).toHaveBeenCalledExactlyOnceWith('ws-a');
  });

  it('follows the selected workspace and ignores registry reloads of the same workspace', () => {
    workspaces.set([workspace('ws-a', 'Workspace A', true, ['p-alpha']), workspace('ws-b', 'Workspace B', false, ['p-beta'])]);
    TestBed.tick();
    load.mockClear();

    activeWorkspaceName.set('Workspace B');
    TestBed.tick();
    expect(load).toHaveBeenCalledExactlyOnceWith('ws-b');

    workspaces.set(workspaces().map(ws => ({ ...ws, projects: [...ws.projects] })));
    TestBed.tick();
    // One board read covers every project of the workspace, so a new project needs no extra read.
    workspaces.set([workspace('ws-a', 'Workspace A', true, ['p-alpha']), workspace('ws-b', 'Workspace B', false, ['p-beta', 'p-gamma'])]);
    TestBed.tick();
    expect(load).toHaveBeenCalledTimes(1);

    activeWorkspaceName.set(null);
    TestBed.tick();
    expect(load).toHaveBeenLastCalledWith('ws-a');
  });

  it('re-reads the current workspace once per visible poll', () => {
    workspaces.set([workspace('ws-a', 'Workspace A', true, ['p-alpha', 'p-beta'])]);
    TestBed.tick();
    load.mockClear();

    vi.advanceTimersByTime(59_999);
    expect(load).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(load).toHaveBeenCalledExactlyOnceWith('ws-a');
  });
});

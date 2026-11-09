import { describe, expect, it, vi } from 'vitest';
import { signal, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { OnboardProjectDialogComponent } from './onboard-project-dialog.component';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { CliCatalogStore } from '../../../cli';
import { RemoteHostsService } from '../../../remote-hosts';
import type { CreateRegistryProjectRequest } from '../../../../models/task.model';

describe('OnboardProjectDialogComponent', () => {
  async function setup() {
    let submitted: CreateRegistryProjectRequest | undefined;
    // Mirrors RemoteHostsService.reload(): it reads and replaces its own host list.
    const hosts = signal<{ id: string }[]>([]);
    const ensureLoaded = vi.fn(() => hosts.set(hosts().map(host => ({ ...host }))));
    const manager = {
      onboardProjectOpen: signal(false), onboardWorkspaceId: signal<string | null>(null),
      closeProjectOnboard: vi.fn(), refreshAfterProjectCreate: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [OnboardProjectDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: WorkspaceManagerService, useValue: manager },
        { provide: TaskService, useValue: {
          createRegistryProject: (body: CreateRegistryProjectRequest) => {
            submitted = body;
            return of({ displayName: body.displayName });
          },
          getRegistryWorkspaces: () => of([]),
        } },
        { provide: NotificationService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: CliCatalogStore, useValue: { ensure: () => of(null), modelsFor: () => [] } },
        { provide: RemoteHostsService, useValue: { ensureLoaded, hosts } },
      ],
    }).overrideComponent(OnboardProjectDialogComponent, { set: { template: '' } }).compileComponents();
    const component = TestBed.createComponent(OnboardProjectDialogComponent).componentInstance;
    component.registryWorkspaces.set([{
      id: 'ws-default', displayName: 'Default', projects: [],
    } as never]);
    component.workspaceId.set('ws-default');
    component.displayName.set('Fresh Site');
    component.shortCode.set('FS');
    return { component, manager, ensureLoaded, submitted: () => submitted };
  }

  it('submits an existing repository without provisioning', async () => {
    const { component, submitted } = await setup();
    component.repositoryPath.set('/work/fresh-site');
    component.repositoryUrl.set('https://github.com/example/fresh-site');
    expect(component.canSubmit()).toBe(true);
    component.onSubmit();
    expect(submitted()).toMatchObject({
      repositoryPath: '/work/fresh-site',
      repositoryUrl: 'https://github.com/example/fresh-site',
    });
    expect(submitted()?.createRepository).toBeUndefined();
  });

  it('submits a template repository without user supplied paths', async () => {
    const { component, submitted } = await setup();
    component.setRepositoryMode('create');
    component.repositoryOwner.set('example');
    component.repositoryName.set('fresh-site');
    component.repositoryVisibility.set('private');
    component.repositoryTemplate.set('container-service');
    expect(component.canSubmit()).toBe(true);
    component.onSubmit();
    expect(submitted()?.createRepository).toEqual({
      owner: 'example', name: 'fresh-site', visibility: 'private', template: 'container-service',
    });
    expect(submitted()?.repositoryPath).toBeUndefined();
    expect(submitted()?.repositoryUrl).toBeUndefined();
  });

  it('loads the host registry once when the dialog opens', async () => {
    const { manager, ensureLoaded } = await setup();
    manager.onboardWorkspaceId.set('ws-default');
    manager.onboardProjectOpen.set(true);
    for (let pass = 0; pass < 5; pass++) TestBed.tick();
    expect(ensureLoaded).toHaveBeenCalledTimes(1);
  });
});

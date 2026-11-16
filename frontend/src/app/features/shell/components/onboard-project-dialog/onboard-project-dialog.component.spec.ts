import { describe, expect, it, vi } from 'vitest';
import { signal, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { OnboardProjectDialogComponent } from './onboard-project-dialog.component';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { CliCatalogStore } from '../../../cli';
import { RemoteHostsService, seedRemoteHosts, type RemoteHost } from '../../../remote-hosts';
import type { CreateRegistryProjectRequest } from '../../../../models/task.model';
import { ServerCheckoutStore } from '../../../server-checkout';

describe('OnboardProjectDialogComponent', () => {
  async function setup() {
    let submitted: CreateRegistryProjectRequest | undefined;
    const manager = {
      onboardProjectOpen: signal(false), onboardWorkspaceId: signal<string | null>(null),
      closeProjectOnboard: vi.fn(), refreshAfterProjectCreate: vi.fn(), notifyRegistryChanged: vi.fn(),
    };
    const serverCheckout = { enabled: signal(false), startForNewProject: vi.fn() };
    const hosts = signal<RemoteHost[]>([]);
    const ensureLoaded = vi.fn(() => hosts.set([...hosts()]));
    const getCapability = vi.fn(() => of({ localCodingAdmitted: true }));
    await TestBed.configureTestingModule({
      imports: [OnboardProjectDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: WorkspaceManagerService, useValue: manager },
        { provide: TaskService, useValue: {
          createRegistryProject: (body: CreateRegistryProjectRequest) => {
            submitted = body;
            return of({ id: 'PROJ-2', displayName: body.displayName, repositoryPath: body.repositoryPath ?? null });
          },
          getRegistryWorkspaces: () => of([]),
        } },
        { provide: NotificationService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: CliCatalogStore, useValue: { ensure: () => of(null), modelsFor: () => [] } },
        { provide: RemoteHostsService, useValue: { ensureLoaded, hosts } },
        { provide: HttpClient, useValue: { get: getCapability } },
        { provide: ServerCheckoutStore, useValue: serverCheckout },
      ],
    }).overrideComponent(OnboardProjectDialogComponent, { set: { template: '' } }).compileComponents();
    const fixture = TestBed.createComponent(OnboardProjectDialogComponent);
    const component = fixture.componentInstance;
    component.registryWorkspaces.set([{
      id: 'ws-default', displayName: 'Default', projects: [],
    } as never]);
    component.workspaceId.set('ws-default');
    component.displayName.set('Fresh Site');
    component.shortCode.set('FS');
    component.executionCapabilityLoaded.set(true);
    component.localCodingAdmitted.set(true);
    component.executionRunner.set('local');
    return { component, fixture, manager, hosts, ensureLoaded, getCapability, serverCheckout, submitted: () => submitted };
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

  it('creates the server checkout of a local URL project in the create flow (AGT-3809)', async () => {
    const { component, manager, serverCheckout, submitted } = await setup();
    serverCheckout.enabled.set(true);
    component.repositoryUrl.set('https://github.com/example/library.git');
    expect(component.offerCheckout()).toBe(true);
    expect(component.createCheckout()).toBe(true);
    component.onSubmit();
    expect(submitted()?.repositoryUrl).toBe('https://github.com/example/library.git');
    expect(serverCheckout.startForNewProject).toHaveBeenCalledWith('PROJ-2');
    expect(component.createdProject()).toEqual({ id: 'PROJ-2', displayName: 'Fresh Site' });
    expect(manager.refreshAfterProjectCreate).not.toHaveBeenCalled();
    component.finish();
    expect(manager.refreshAfterProjectCreate).toHaveBeenCalledTimes(1);
    expect(component.createdProject()).toBeNull();
  });

  it('skips the checkout when the owner opts out or supplies a checkout path', async () => {
    const { component, manager, serverCheckout } = await setup();
    serverCheckout.enabled.set(true);
    component.repositoryUrl.set('https://github.com/example/library.git');
    component.repositoryPath.set('/work/library');
    expect(component.offerCheckout()).toBe(false);
    component.repositoryPath.set('');
    component.createCheckout.set(false);
    component.onSubmit();
    expect(serverCheckout.startForNewProject).not.toHaveBeenCalled();
    expect(manager.refreshAfterProjectCreate).toHaveBeenCalledTimes(1);
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

  it('requires a runner for a checkout when local coding is disabled', async () => {
    const { component, submitted, hosts } = await setup();
    hosts.set([{ ...seedRemoteHosts(0)[0], id: 'agent-runner-01', name: 'Coding runner',
      role: 'remote', serviceRole: 'coding', status: 'online' }]);
    component.repositoryPath.set('/work/fresh-site');
    component.localCodingAdmitted.set(false);
    component.executionRunner.set('local');
    expect(component.codingNeedsRunner()).toBe(true);
    expect(component.canSubmit()).toBe(false);
    component.onSubmit();
    expect(submitted()).toBeUndefined();
    component.executionRunner.set('agent-runner-01');
    expect(component.canSubmit()).toBe(true);
  });

  it('rejects review and unavailable coding runners', async () => {
    const { component, hosts } = await setup();
    component.localCodingAdmitted.set(false);
    hosts.set([
      { ...seedRemoteHosts(0)[0], id: 'review', name: 'Review runner', role: 'remote', serviceRole: 'review', status: 'online' },
      { ...seedRemoteHosts(0)[0], id: 'offline', name: 'Offline runner', role: 'remote', serviceRole: 'coding', status: 'offline' },
      { ...seedRemoteHosts(0)[0], id: 'coding', name: 'Coding runner', role: 'remote', serviceRole: 'coding', status: 'online' },
    ]);
    component.executionRunner.set('review');
    expect(component.canSubmit()).toBe(false);
    expect(component.runnerUnavailableReason(hosts()[0])).toBe('Review-only runner');
    component.executionRunner.set('offline');
    expect(component.canSubmit()).toBe(false);
    component.executionRunner.set('coding');
    expect(component.canSubmit()).toBe(true);
  });

  it('loads execution capability once when the host registry updates', async () => {
    const { fixture, manager, hosts, ensureLoaded, getCapability } = await setup();
    fixture.detectChanges();
    manager.onboardWorkspaceId.set('ws-default');
    manager.onboardProjectOpen.set(true);
    await fixture.whenStable();
    hosts.set([...hosts()]);
    await fixture.whenStable();
    expect(ensureLoaded).toHaveBeenCalledTimes(1);
    expect(getCapability).toHaveBeenCalledTimes(1);
  });
});

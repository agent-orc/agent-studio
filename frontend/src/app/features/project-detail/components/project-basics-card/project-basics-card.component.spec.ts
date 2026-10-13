import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import type { RegistryProjectSummary, RegistryWorkspaceListItem } from '../../../../models/task.model';
import { CliCatalogStore } from '../../../cli';
import { NotificationService } from '../../../../services/notification.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { TaskService } from '../../../../services/task.service';
import { WorkspaceManagerService } from '../../../shell';
import { ProjectOverlaysService } from '../../state/project-overlays.service';
import { ProjectBasicsCardComponent } from './project-basics-card.component';

describe('ProjectBasicsCardComponent (smoke)', () => {
  it('compiles and instantiates', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectBasicsCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBasicsCardComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('falls a blank working directory back to the repository path in the atomic patch', async () => {
    const project: RegistryProjectSummary = {
      sourceType: 'local-folder',
      id: 'PROJ-001',
      displayName: 'Demo',
      shortCode: 'DEM',
      workspaceId: 'ws-1',
      color: '#569cd6',
      cliDefault: null,
      modelDefault: null,
      sortOrder: 0,
      storageLocation: 'C:/task-store/PROJ-001/tasks',
      repositoryPath: null,
      rootPath: null,
      repositoryUrl: null,
      urls: [],
      archived: false,
      createdAt: '2026-01-01T00:00:00Z',
    };
    const updated = {
      ...project,
      repositoryPath: 'C:/Projects/demo',
      rootPath: 'C:/Projects/demo',
    };
    const workspaces = [{
      id: 'ws-1',
      displayName: 'Workspace',
      sortOrder: 0,
      isDefault: true,
      color: null,
      createdAt: '2026-01-01T00:00:00Z',
      projects: [updated],
    }] satisfies RegistryWorkspaceListItem[];
    const taskStub = {
      updateRegistryProject: vi.fn(() => of(updated)),
      getRegistryWorkspaces: vi.fn(() => of(workspaces)),
      getAllProjectSettings: vi.fn(() => of({ Demo: { executionLocation: 'local' } })),
    };

    await TestBed.configureTestingModule({
      imports: [ProjectBasicsCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: TaskService, useValue: taskStub },
        { provide: CliCatalogStore, useValue: { ensure: () => of([]), modelsFor: () => [] } },
        { provide: NotificationService, useValue: { success: vi.fn() } },
        { provide: ProjectLookupService, useValue: { setWorkspaces: vi.fn(), getProjectDisplay: () => ({ id: project.id }) } },
        { provide: WorkspaceManagerService, useValue: { notifyRegistryChanged: vi.fn(), notifyProjectRenamed: vi.fn() } },
        { provide: ProjectOverlaysService, useValue: { renameOpenProjectShell: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBasicsCardComponent);
    fixture.componentRef.setInput('projectName', project.displayName);
    const component = fixture.componentInstance;
    component.project.set(project);
    component.workspaces.set(workspaces);
    component.loading.set(false);
    component.workspaceId.set(project.workspaceId);
    component.displayName.set(project.displayName);
    component.shortCode.set(project.shortCode);
    component.color.set(project.color!);
    component.repositoryPath.set('C:/Projects/demo');
    component.rootPath.set('');

    component.save();

    expect(taskStub.updateRegistryProject).toHaveBeenCalledWith(project.id, expect.objectContaining({
      repositoryPath: 'C:/Projects/demo',
      rootPath: 'C:/Projects/demo',
      clearRootPath: false,
    }));
  });

  it('warns when the project is remotely assigned without repositoryUrl', async () => {
    const project: RegistryProjectSummary = {
      sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM',
      workspaceId: 'ws-1', color: '#569cd6', cliDefault: null, modelDefault: null,
      sortOrder: 0, storageLocation: '/tasks', repositoryPath: null, rootPath: null,
      repositoryUrl: null, urls: [], archived: false, createdAt: '2026-01-01T00:00:00Z',
    };
    const workspaces = [{
      id: 'ws-1', displayName: 'Workspace', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-01-01T00:00:00Z', projects: [project],
    }] satisfies RegistryWorkspaceListItem[];
    const taskStub = {
      getRegistryWorkspaces: vi.fn(() => of(workspaces)),
      getAllProjectSettings: vi.fn(() => of({ Demo: { executionLocation: 'agent-runner-01' } })),
    };
    await TestBed.configureTestingModule({
      imports: [ProjectBasicsCardComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: TaskService, useValue: taskStub },
        { provide: CliCatalogStore, useValue: { ensure: () => of([]), modelsFor: () => [] } },
        { provide: NotificationService, useValue: { success: vi.fn() } },
        { provide: ProjectLookupService, useValue: { setWorkspaces: vi.fn(), getProjectDisplay: () => ({ id: project.id }) } },
        { provide: WorkspaceManagerService, useValue: { notifyRegistryChanged: vi.fn(), notifyProjectRenamed: vi.fn() } },
        { provide: ProjectOverlaysService, useValue: { renameOpenProjectShell: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBasicsCardComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/projects/Demo/checkout/status')
      .flush({ status: 'up-to-date', branch: 'develop', behind: 0, dirtyFiles: [], lockFile: null, reason: null });
    await fixture.whenStable();
    fixture.detectChanges();

    const warning = fixture.nativeElement.querySelector(
      '[data-testid="project-remote-repository-warning"]',
    ) as HTMLElement;
    expect(warning.textContent).toContain('Remote execution is not claimable');
    expect(warning.textContent).toContain('repositoryUrl is missing');
  });
});

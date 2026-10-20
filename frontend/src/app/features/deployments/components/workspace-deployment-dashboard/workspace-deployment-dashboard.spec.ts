import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { DEPLOYMENT_CLOCK, DeploymentStatusService } from '../../services/deployment-status.service';
import { WorkspaceDeploymentDashboardComponent } from './workspace-deployment-dashboard';
import type { RegistryWorkspaceListItem, RegistryProjectSummary } from '../../../../models/task.model';

const fixedNow = Date.parse('2026-10-16T12:00:00Z');
const project = (id: string, displayName: string): RegistryProjectSummary => ({
  id, displayName, sourceType: 'local-folder', shortCode: id, workspaceId: 'group-a', color: null,
  cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: `/repo/${id}`,
  repositoryPath: null, rootPath: null, repositoryUrl: null, urls: [], archived: false,
  createdAt: '2026-10-01T00:00:00Z',
});
const group: RegistryWorkspaceListItem = {
  id: 'group-a', displayName: 'Web', sortOrder: 0, isDefault: true, color: null,
  createdAt: '2026-10-01T00:00:00Z', projects: [project('site', 'Website Registry')],
};

describe('WorkspaceDeploymentDashboardComponent', () => {
  it('renders typed fixture statuses, filters both axes, and opens the project deployment area', async () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: DEPLOYMENT_CLOCK, useValue: () => fixedNow }],
    });
    const fixture = TestBed.createComponent(WorkspaceDeploymentDashboardComponent);
    fixture.componentRef.setInput('projectNames', ['Website', 'Other']);
    fixture.componentRef.setInput('projectStorageByName', new Map([['Website', '/repo/site']]));
    fixture.componentRef.setInput('workspaces', [group]);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Release 2026.10.16.3');
    expect(root.textContent).toContain('Newer release');
    expect(root.textContent).toContain('Awaiting approval');
    expect(root.textContent).toContain('1h ago');
    const selected: string[] = [];
    fixture.componentInstance.openDeployment.subscribe(name => selected.push(name));
    const production = root.querySelector('[aria-label="Website Production deployment"]') as HTMLButtonElement;
    const stateBeforeFilter = production.dataset['state'];
    production.click();
    expect(selected).toEqual(['Website']);
    const groupSelect = root.querySelector('[data-testid="deployment-group-filter"]') as HTMLSelectElement;
    groupSelect.value = 'group-a';
    groupSelect.dispatchEvent(new Event('change'));
    const environmentSelect = root.querySelector('[data-testid="deployment-environment-filter"]') as HTMLSelectElement;
    environmentSelect.value = 'production';
    environmentSelect.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(root.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(root.querySelectorAll('tbody td')).toHaveLength(1);
    expect(root.textContent).not.toContain('Other');
    expect((root.querySelector('[aria-label="Website Production deployment"]') as HTMLButtonElement).dataset['state']).toBe(stateBeforeFilter);
  });

  it('keeps environments ordered and exposes approval, failure, and health separately', () => {
    TestBed.configureTestingModule({ providers: [{ provide: DEPLOYMENT_CLOCK, useValue: () => fixedNow }] });
    const service = TestBed.inject(DeploymentStatusService);
    const statuses = service.statuses('site', 1);
    expect(statuses.map(status => status.environment.kind)).toEqual(['staging', 'production']);
    expect(statuses[1].currentDeployment?.state).toBe('awaiting-approval');
    expect(statuses[1].newerRelease?.id).not.toBe(statuses[1].currentRelease?.id);
    expect(service.statuses('site', 2)[0].health).toBe('unhealthy');
  });
});

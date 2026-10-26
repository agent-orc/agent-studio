import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { DEPLOYMENT_CLOCK } from '../../services/deployment-status.service';
import { WorkspaceDeploymentDashboardComponent } from './workspace-deployment-dashboard';
import type { RegistryWorkspaceListItem, RegistryProjectSummary } from '../../../../models/task.model';
import type { EnvironmentStatus } from '../../models/deployment.model';

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
const status = (kind: 'staging' | 'production', tenant: string | null, state: 'succeeded' | 'awaiting-approval'): EnvironmentStatus => ({
  environment: { id: kind, projectId: 'Website', name: kind, order: kind === 'staging' ? 0 : 1, kind },
  currentRelease: { id: 'release', projectId: 'Website', version: '2026.10.16.3', commitSha: 'a'.repeat(40),
    artifactDigests: [], processVersion: '1', variablesVersion: '1', cardKeys: [],
    createdAtUtc: '2026-10-16T09:00:00Z', channel: 'default' },
  currentDeployment: { id: `${kind}-${tenant}`, releaseId: 'release', environmentId: kind, state,
    startedAtUtc: '2026-10-16T10:00:00Z', finishedAtUtc: state === 'succeeded' ? '2026-10-16T11:00:00Z' : null,
    approvedBy: null, steps: [], tenant },
  newerRelease: null, cardsSinceLastDeploy: [], health: 'healthy', tenant,
});

describe('WorkspaceDeploymentDashboardComponent', () => {
  it('projects live matrix cells by tenant and filters both axes', async () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: DEPLOYMENT_CLOCK, useValue: () => fixedNow }],
    });
    const fixture = TestBed.createComponent(WorkspaceDeploymentDashboardComponent);
    fixture.componentRef.setInput('projectNames', ['Website', 'Other']);
    fixture.componentRef.setInput('projectStorageByName', new Map([['Website', '/repo/site']]));
    fixture.componentRef.setInput('workspaces', [group]);
    TestBed.inject(HttpTestingController).expectOne('/api/deploy/matrix').flush({
      environmentKinds: ['staging', 'production'], projects: [{ projectId: 'Website', error: null,
        statuses: [status('staging', null, 'succeeded'), status('production', null, 'succeeded')],
        tenantStatuses: [status('staging', 'acme', 'succeeded'), status('production', 'acme', 'awaiting-approval'),
          status('staging', 'beta', 'succeeded'), status('production', 'beta', 'succeeded')] }],
    });
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve, 0));
    const http = TestBed.inject(HttpTestingController);
    for (const name of ['Website', 'Other']) {
      http.expectOne(`/api/projects/${name}/deploy/delivery-lanes`)
        .flush({ projectId: name, lanes: [] });
    }
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('tbody tr')).toHaveLength(3);
    expect(root.textContent).toContain('Release 2026.10.16.3');
    expect(root.textContent).toContain('Awaiting approval');
    expect(root.textContent).toContain('1h ago');
    const selected: string[] = [];
    fixture.componentInstance.openDeployment.subscribe(name => selected.push(name));
    (root.querySelector('[aria-label="Website acme Production deployment"]') as HTMLButtonElement).click();
    expect(selected).toEqual(['Website']);
    const tenantSelect = root.querySelector('[data-testid="deployment-tenant-filter"]') as HTMLSelectElement;
    tenantSelect.value = 'acme';
    tenantSelect.dispatchEvent(new Event('change'));
    const environmentSelect = root.querySelector('[data-testid="deployment-environment-filter"]') as HTMLSelectElement;
    environmentSelect.value = 'production';
    environmentSelect.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(root.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(root.querySelectorAll('tbody td')).toHaveLength(1);
    expect(root.querySelector('tbody')?.textContent).not.toContain('beta');
    expect((root.querySelector('[aria-label="Website acme Production deployment"]') as HTMLButtonElement).dataset['state'])
      .toBe('awaiting-approval');
    TestBed.inject(HttpTestingController).verify();
  });
});

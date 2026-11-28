import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { DEPLOYMENT_CLOCK } from '../../services/deployment-status.service';
import { WorkspaceDeploymentDashboardComponent, dashboardHealth, dashboardStateTone } from './workspace-deployment-dashboard';
import type { RegistryWorkspaceListItem, RegistryProjectSummary } from '../../../../models/task.model';
import type { EnvironmentStatus, InstanceLastRun } from '../../models/deployment.model';
import type { DeployMatrixRow } from '../../services/deployment-status.service';

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

/** The running Studio instance as the only environment of its source project (AGT-3949). */
const instanceStatus = (lastRun: Partial<InstanceLastRun> | null, overrides: Partial<EnvironmentStatus> = {}): EnvironmentStatus => ({
  environment: { id: 'studio-instance', projectId: 'Agent Studio', name: 'Production', order: 0, kind: 'production' },
  currentRelease: { id: 'instance-1', projectId: 'Agent Studio', version: '0.9.5', commitSha: '5faca674a'.padEnd(40, '0'),
    artifactDigests: [], processVersion: 'instance', variablesVersion: 'instance', cardKeys: [],
    createdAtUtc: '2026-10-16T09:00:00Z', channel: 'default' },
  currentDeployment: { id: 'run-1', releaseId: 'instance-1', environmentId: 'studio-instance',
    state: lastRun?.state === 'stalled' ? 'failed' : lastRun?.state ?? 'succeeded',
    startedAtUtc: '2026-10-16T08:50:00Z', finishedAtUtc: '2026-10-16T09:00:00Z', approvedBy: null, steps: [] },
  newerRelease: null, cardsSinceLastDeploy: [], health: 'healthy', releaseUncertain: false,
  instance: { liveSinceUtc: '2026-10-16T09:00:00Z', lastRun: lastRun === null ? null : {
    id: 'run-1', state: 'succeeded', version: '0.9.5', commitSha: '5faca674a'.padEnd(40, '0'),
    startedAtUtc: '2026-10-16T08:50:00Z', atUtc: '2026-10-16T09:00:00Z', step: 'switch:server', ...lastRun } },
  ...overrides,
});

const matrixRow = (projectId: string, statuses: EnvironmentStatus[], environmentsDeclared = true): DeployMatrixRow =>
  ({ projectId, statuses, tenantStatuses: [], error: null, environmentsDeclared });

/** Renders the dashboard over one matrix answer; delivery lane requests are answered empty. */
async function render(projectNames: string[], projects: DeployMatrixRow[] | 'fail') {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: DEPLOYMENT_CLOCK, useValue: () => fixedNow }],
  });
  const fixture = TestBed.createComponent(WorkspaceDeploymentDashboardComponent);
  fixture.componentRef.setInput('projectNames', projectNames);
  fixture.componentRef.setInput('workspaces', []);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  const loading = root.querySelector('[data-testid="deployment-empty"]')?.textContent?.trim();
  const request = http.expectOne('/api/deploy/matrix');
  if (projects === 'fail') request.flush('down', { status: 503, statusText: 'Service Unavailable' });
  else request.flush({ environmentKinds: ['staging', 'production'], projects });
  fixture.detectChanges();
  await new Promise(resolve => setTimeout(resolve, 0));
  const lanes = http.match(candidate => candidate.url.endsWith('/deploy/delivery-lanes'));
  for (const lane of lanes) lane.flush({ projectId: decodeURIComponent(lane.request.url.split('/')[3]), lanes: [] });
  await fixture.whenStable();
  return { fixture, root, http, loading, laneUrls: lanes.map(lane => lane.request.url) };
}

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
    // 'Other' is known to the shell but has no matrix row: this instance does not watch it.
    http.expectOne('/api/projects/Website/deploy/delivery-lanes').flush({ projectId: 'Website', lanes: [] });
    http.expectNone('/api/projects/Other/deploy/delivery-lanes');
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
    const cell = root.querySelector('[aria-label="Website acme Production deployment"]') as HTMLElement;
    expect(cell.querySelector<HTMLElement>('[data-testid="deployment-dashboard-state"]')?.dataset['tone']).toBe('warning');
    expect(cell.querySelector<HTMLElement>('[data-testid="deployment-dashboard-approval"]')?.dataset['tone']).toBe('warning');
    TestBed.inject(HttpTestingController).verify();
  });

  it('maps deployment states and health onto tones', () => {
    const states: Record<string, string> = {
      queued: 'neutral', running: 'running', succeeded: 'success', failed: 'danger',
      cancelled: 'neutral', 'awaiting-approval': 'warning', later: 'neutral',
    };
    for (const [state, tone] of Object.entries(states)) expect(dashboardStateTone(state)).toBe(tone);
    expect(dashboardStateTone('stalled')).toBe('warning');
    expect(dashboardStateTone(undefined)).toBe('neutral');
    expect(dashboardHealth('healthy')).toEqual({ tone: 'success', label: 'Healthy' });
    expect(dashboardHealth('unhealthy')).toEqual({ tone: 'danger', label: 'Unhealthy' });
    expect(dashboardHealth('unknown')).toEqual({ tone: 'neutral', label: 'Unknown' });
    expect(dashboardHealth('later').tone).toBe('neutral');
  });

  it('does not present a failed rollback target or stale release as running', async () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: DEPLOYMENT_CLOCK, useValue: () => fixedNow }],
    });
    const fixture = TestBed.createComponent(WorkspaceDeploymentDashboardComponent);
    fixture.componentRef.setInput('projectNames', ['Website']);
    fixture.componentRef.setInput('workspaces', [group]);
    const failed = { ...status('production', null, 'succeeded'), releaseUncertain: true,
      currentDeployment: { ...status('production', null, 'succeeded').currentDeployment!, state: 'failed' as const } };
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/deploy/matrix').flush({ environmentKinds: ['production'], projects: [
      { projectId: 'Website', error: null, statuses: [failed], tenantStatuses: [] },
    ] });
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve, 0));
    http.expectOne('/api/projects/Website/deploy/delivery-lanes').flush({ projectId: 'Website', lanes: [] });
    await fixture.whenStable();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Running release uncertain. A failed release may be running.');
    expect(text).toContain('Deploy time unknown');
    expect(text).not.toContain('Release 2026.10.16.3');
  });
  it('shows the Studio instance as the Production cell of its source project', async () => {
    const { root } = await render(['Agent Studio'], [matrixRow('Agent Studio', [instanceStatus({})], false)]);
    const row = root.querySelector('[data-testid="deployment-project-Agent Studio"]') as HTMLElement;
    expect(row).not.toBeNull();
    const cell = row.querySelector('[aria-label="Agent Studio Production deployment"]') as HTMLElement;
    expect(cell.textContent).toContain('Release 0.9.5');
    expect(cell.textContent).toContain('Live 3h ago');
    expect(cell.dataset['state']).toBe('succeeded');
    expect(cell.querySelector<HTMLElement>('[data-testid="deployment-dashboard-state"]')?.dataset['tone']).toBe('success');
    expect(cell.querySelector<HTMLElement>('[data-testid="deployment-dashboard-health"]')?.textContent).toContain('Healthy');
    expect(cell.querySelector('[data-testid="deployment-dashboard-instance"]')?.textContent?.trim())
      .toBe('This Studio instance · commit 5faca674a');
    // The Studio has no staging environment, and the row does not ask to declare one.
    expect(row.querySelector('td')?.textContent).toContain('No staging environment');
    expect(row.querySelector('[data-testid^="deployment-undeclared-"]')).toBeNull();
  });

  it('names a stalled, a failed and a running instance deploy with its step', async () => {
    const stalled = instanceStatus({ state: 'stalled', version: '0.9.6', step: 'build:server' }, { health: 'unhealthy' });
    const { root, fixture } = await render(['Agent Studio'], [matrixRow('Agent Studio', [stalled], false)]);
    const cell = () => root.querySelector('[aria-label="Agent Studio Production deployment"]') as HTMLElement;
    const state = () => cell().querySelector<HTMLElement>('[data-testid="deployment-dashboard-state"]');
    expect(cell().dataset['state']).toBe('stalled');
    expect(state()?.textContent).toContain('Stalled');
    expect(state()?.dataset['tone']).toBe('warning');
    // The stalled deploy never reached the switch: the live release is still named.
    expect(cell().textContent).toContain('Release 0.9.5');
    expect(cell().textContent).toContain('deploy of 0.9.6 stalled at build:server');
    expect(cell().querySelector<HTMLElement>('[data-testid="deployment-dashboard-health"]')?.dataset['tone']).toBe('danger');

    const instance = fixture.componentInstance;
    expect(instance.instanceNote(instanceStatus({ state: 'failed', version: '0.9.6', step: 'host:runner-1' })))
      .toBe('This Studio instance · commit 5faca674a · deploy of 0.9.6 failed at host:runner-1');
    expect(instance.instanceNote(instanceStatus({ state: 'running', version: '0.9.6', step: 'build:server' })))
      .toBe('This Studio instance · commit 5faca674a · deploy of 0.9.6 running');
    const never = instanceStatus(null, { currentRelease: null, currentDeployment: null, health: 'unknown',
      instance: { liveSinceUtc: null, lastRun: null } });
    expect(instance.instanceNote(never)).toBe('This Studio instance');
    expect(instance.age(never)).toBe('Not live yet');
    expect(instance.cellState(never)).toBeUndefined();
  });

  it('lists a watched project without environments and says how to declare them', async () => {
    const { root } = await render(['Docs', 'Website'], [
      matrixRow('Docs', [], false),
      matrixRow('Website', [status('production', null, 'succeeded')]),
    ]);
    expect(root.querySelectorAll('tbody tr')).toHaveLength(1);
    const details = root.querySelector('details') as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(details.querySelector('summary')?.textContent).toContain('1 project has no deployment target');
    expect(details.textContent).toContain('Docs');
    expect(details.textContent).toContain('docs/deployments/environments');
    const link = details.querySelector('a') as HTMLAnchorElement;
    expect(link.textContent).toContain('How to declare environments');
    expect(link.href).toContain('/docs/concepts/deployment-first-class.md');
    expect(link.rel).toContain('noopener');
    // A declared project without one of the two kinds says so in that cell.
    expect(root.querySelector('[data-testid="deployment-project-Website"] td')?.textContent)
      .toContain('No staging environment');
    expect(root.querySelector('[data-testid="deployment-empty"]')).toBeNull();
  });

  it('shows a broken declaration with its error instead of an empty row', async () => {
    const { root } = await render(['Website'], [
      { ...matrixRow('Website', []), error: 'production.json: unexpected token' }]);
    const note = root.querySelector('[data-testid="deployment-declaration-error-Website"]') as HTMLElement;
    expect(note.textContent).toContain('Environment declarations could not be read');
    expect(note.textContent).toContain('production.json: unexpected token');
  });

  it('requests delivery lanes only for projects this instance watches', async () => {
    // Runbook is registered in the workspace, but the matrix of this instance has no row for it.
    const { root, laneUrls } = await render(['Agent Studio', 'Runbook'], [matrixRow('Agent Studio', [instanceStatus({})], false)]);
    expect(laneUrls).toEqual(['/api/projects/Agent%20Studio/deploy/delivery-lanes']);
    expect(root.querySelectorAll('[data-testid="delivery-lanes"]')).toHaveLength(1);
    expect(root.textContent).not.toContain('Runbook');
    expect(root.textContent).not.toContain('could not be loaded');
  });

  it('shows a watched project the shell does not list by that name', async () => {
    const { root, laneUrls } = await render([], [matrixRow('Docs', [], false)]);
    expect(root.querySelector('[data-testid="deployment-undeclared-Docs"]')).not.toBeNull();
    expect(laneUrls).toEqual([]);
  });

  it('says why the table is empty', async () => {
    const empty = await render(['Runbook'], []);
    expect(empty.loading).toBe('Loading deployments…');
    expect(empty.root.querySelector('[data-testid="deployment-empty"]')?.textContent)
      .toContain('This instance watches no project you have access to');
    expect(empty.laneUrls).toEqual([]);
    TestBed.resetTestingModule();

    const failed = await render(['Website'], 'fail');
    expect(failed.root.querySelector('[data-testid="deployment-empty"]')?.textContent)
      .toContain('The deployment status could not be loaded');
    TestBed.resetTestingModule();

    const filtered = await render(['Docs'], [matrixRow('Docs', [], false)]);
    filtered.fixture.componentInstance.tenantFilter.set('acme');
    await filtered.fixture.whenStable();
    expect(filtered.root.querySelector('[data-testid="deployment-empty"]')?.textContent)
      .toContain('No project matches the selected filters');
  });
});

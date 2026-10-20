import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideZonelessChangeDetection } from '@angular/core';
import { DeferBlockState, TestBed, type ComponentFixture } from '@angular/core/testing';
import { BehaviorSubject, Subject, map, type Observable } from 'rxjs';
import { vi } from 'vitest';
import { ProjectDeployAreaComponent } from './project-deploy-area';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { DEPLOY_CLOCK, type DeployPanel } from '../../state/deploy-area.model';
import {
  DEPLOY_FIXTURE_NOW,
  deployDeploymentsFixture,
  deployEnvironmentsFixture,
  deployReleasesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  pipelineDefinitionFixture,
} from '../../testing/deploy-fixtures';

class FakeDeployData {
  readonly environmentsPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly statusPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly releasesPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly deploymentsPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly pipelinePanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly healthPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'unavailable' });
  readonly requests: string[] = [];
  readonly actions: string[] = [];
  actionResult = new Subject<unknown>();
  controlledReads = false;
  readonly statusReads: Subject<DeployPanel<unknown>>[] = [];
  readonly deploymentReads: Subject<DeployPanel<unknown>>[] = [];

  environments(project: string): Observable<DeployPanel<unknown>> { this.requests.push(`environments:${project}`); return this.environmentsPanel; }
  statuses(): Observable<DeployPanel<unknown>> {
    this.requests.push('status');
    if (!this.controlledReads) return this.statusPanel;
    const read = new Subject<DeployPanel<unknown>>();
    this.statusReads.push(read);
    return read;
  }
  releases(): Observable<DeployPanel<unknown>> { return this.releasesPanel; }
  deployments(): Observable<DeployPanel<unknown>> { this.requests.push('deployments'); return this.deploymentsPanel; }
  releasePage(_project: string, offset: number): Observable<DeployPanel<unknown>> {
    this.requests.push(`releases:${offset}`);
    return this.releasesPanel.pipe(map(panel => panel.status === 'ready'
      ? { status: 'ready', value: { items: (panel.value as unknown[]).slice(offset, offset + 25), total: (panel.value as unknown[]).length, offset, limit: 25 } }
      : panel));
  }
  deploymentPage(_project: string, offset: number, filter?: { environmentId: string; state: string; period: string }): Observable<DeployPanel<unknown>> {
    this.requests.push(`deployments:${offset}:${filter?.environmentId ?? 'all'}`);
    if (this.controlledReads) {
      const read = new Subject<DeployPanel<unknown>>();
      this.deploymentReads.push(read);
      return read;
    }
    return this.deploymentsPanel.pipe(map(panel => {
      if (panel.status !== 'ready') return panel;
      let items = panel.value as { environmentId: string; state: string }[];
      if (filter?.environmentId && filter.environmentId !== 'all') items = items.filter(item => item.environmentId === filter.environmentId);
      if (filter?.state && filter.state !== 'all') items = items.filter(item => item.state === filter.state);
      return { status: 'ready', value: { items: items.slice(offset, offset + 25), total: items.length, offset, limit: 25 } };
    }));
  }
  pipeline(): Observable<DeployPanel<unknown>> { return this.pipelinePanel; }
  health(): Observable<DeployPanel<unknown>> { return this.healthPanel; }
  deploymentLog(_project: string, id: string): Observable<DeployPanel<unknown>> {
    this.requests.push(`log:${id}`);
    return new BehaviorSubject<DeployPanel<unknown>>({ status: 'ready', value: deploymentLogFixture(id) });
  }
  act(_project: string, id: string, action: string): Observable<unknown> {
    this.actions.push(`${action}:${id}`);
    return this.actionResult;
  }

  serveEverything(): void {
    this.environmentsPanel.next({ status: 'ready', value: deployEnvironmentsFixture() });
    this.statusPanel.next({ status: 'ready', value: deployStatusFixture() });
    this.releasesPanel.next({ status: 'ready', value: deployReleasesFixture() });
    this.deploymentsPanel.next({ status: 'ready', value: deployDeploymentsFixture() });
    this.pipelinePanel.next({ status: 'ready', value: pipelineDefinitionFixture() });
  }

  /** A server without AGT-3201: its routes are missing. */
  serveHostOnly(): void {
    this.environmentsPanel.next({ status: 'ready', value: deployEnvironmentsFixture() });
    for (const panel of [this.statusPanel, this.releasesPanel, this.deploymentsPanel]) panel.next({ status: 'unavailable' });
  }
}

describe('ProjectDeployAreaComponent', () => {
  let fixture: ComponentFixture<ProjectDeployAreaComponent>;
  let data: FakeDeployData;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const text = (id: string) => byTestId(id)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    for (const block of await fixture.getDeferBlocks()) await block.render(DeferBlockState.Complete);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function click(id: string): Promise<void> {
    byTestId(id)!.click();
    await settle();
  }

  beforeEach(async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    data = new FakeDeployData();
    await TestBed.configureTestingModule({
      imports: [ProjectDeployAreaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ProjectDeployDataService, useValue: data },
        { provide: DEPLOY_CLOCK, useValue: () => DEPLOY_FIXTURE_NOW },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployAreaComponent);
    fixture.componentRef.setInput('projectName', 'Website');
    document.body.appendChild(host());
    await settle();
  });

  afterEach(() => { host().remove(); vi.unstubAllGlobals(); });

  it('shows no preview or fixture data while sources load', () => {
    expect(host().textContent).not.toContain('Preview data');
    expect(host().querySelector('app-loading-surface')).not.toBeNull();
    expect(host().querySelector('app-deploy-environment-card')).toBeNull();
  });

  it('works with partial data: AGT-3176 environments only, AGT-3201 routes missing', async () => {
    data.serveHostOnly();
    await settle();

    expect(host().querySelectorAll('app-deploy-environment-card')).toHaveLength(2);
    expect(text('deploy-environment-stage')).toContain('website@sha256:cccccccccccc');
    expect(text('deploy-environment-stage')).toContain('Healthy');
    expect(text('deploy-environment-stage')).toContain('Card lists arrive with release records');

    await click('deploy-tab-releases');
    expect(byTestId('deploy-releases-unavailable')).not.toBeNull();
    await click('deploy-tab-history');
    expect(byTestId('deploy-history-unavailable')).not.toBeNull();
    await click('deploy-tab-pipeline');
    expect(host().querySelector('app-loading-surface')).not.toBeNull();
  });

  it('reads cards, approval, and history from the AGT-3201 deploy routes', async () => {
    data.serveEverything();
    await settle();

    expect(text('deploy-environment-stage')).toContain('Release 2026.10.19.2');
    expect(text('deploy-environment-production')).toContain('Awaiting approval: Release 2026.10.19.2');
    await click('deploy-tab-history');
    expect(host().querySelectorAll('[data-testid^="deploy-run-row-"]')).toHaveLength(7);
    await click('deploy-tab-pipeline');
    expect(byTestId('pipeline-node-production')).not.toBeNull();
  });

  it('keeps the other panels and shows the error when a deploy route fails', async () => {
    data.environmentsPanel.next({ status: 'error', message: 'Environments could not be loaded.' });
    data.releasesPanel.next({ status: 'ready', value: deployReleasesFixture() });
    data.deploymentsPanel.next({ status: 'error', message: 'Deploy runs could not be loaded.' });
    await settle();

    expect(byTestId('deploy-environments-error')).not.toBeNull();
    await click('deploy-tab-releases');
    expect(byTestId('deploy-release-detail')).not.toBeNull();
    await click('deploy-tab-history');
    expect(text('deploy-history-error')).toContain('Deploy runs could not be loaded.');
  });

  it('shows source-specific status errors while retaining host environment cards', async () => {
    data.serveHostOnly();
    data.statusPanel.next({ status: 'error', message: 'Status could not be read.' });
    data.releasesPanel.next({ status: 'error', message: 'Release store is busy.' });
    await settle();

    expect(text('deploy-source-notices')).toContain('Environment status: Status could not be read.');
    expect(text('deploy-source-notices')).toContain('Releases: Release store is busy.');
    expect(text('deploy-environment-stage')).toContain('website@sha256:cccccccccccc');
  });

  it('uses the health endpoint state on the environment card', async () => {
    data.serveEverything();
    data.healthPanel.next({ status: 'ready', value: {
      capturedAt: '', probeOrigin: '', environments: [{ environmentId: 'stage', status: 'degraded', checks: [{ id: 'http' }] }],
    } });
    await settle();
    expect(text('deploy-environment-stage')).toContain('Degraded');
  });

  it('builds the overview from AGT-3201 status when the environments read fails', async () => {
    data.serveEverything();
    data.environmentsPanel.next({ status: 'error', message: 'Environments could not be loaded.' });
    await settle();

    expect(byTestId('deploy-environments-error')).toBeNull();
    expect(text('deploy-environment-production')).toContain('Release 2026.10.18.1');
  });

  it('opens a run from a status card with its step log and returns focus to the opener on close', async () => {
    data.serveEverything();
    await settle();
    const opener = host().querySelector('[aria-label="Open last run for Staging"]') as HTMLButtonElement;
    opener.focus();
    opener.click();
    await settle();

    expect(text('deploy-run-drawer')).toContain('Redeploy to Staging');
    expect(data.requests).toContain('log:run-c-stage-redeploy');
    expect(host().querySelector('.deploy-area__body--drawer')).not.toBeNull();

    (host().querySelector('[data-testid="sidesheet-close"]') as HTMLButtonElement).click();
    await settle();
    await Promise.resolve();
    expect(byTestId('deploy-run-drawer')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('opens a run from the history table and marks its row', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-tab-history');
    await click('deploy-run-open-run-b-stage');

    expect(text('deploy-run-outcome')).toBe('Failed');
    expect(text('deploy-run-step-health')).toContain('Health check failed (health-http-failed).');
    expect(byTestId('deploy-run-row-run-b-stage')?.getAttribute('aria-current')).toBe('true');
  });

  it('approves a waiting run, then reloads status, runs, and the step log', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-environment-review-approval');
    data.requests.length = 0;
    await click('deploy-run-approve');

    expect(data.actions).toEqual(['approve:run-c-production']);
    expect((byTestId('deploy-run-reject') as HTMLButtonElement).disabled).toBe(true);
    data.actionResult.next(deployDeploymentsFixture()[1]);
    await settle();
    expect(data.requests).toEqual(['status', 'deployments:0:all', 'deployments:0:all', 'log:run-c-production']);
    // The table stays while the reload is in flight.
    expect(byTestId('deploy-run-drawer')).not.toBeNull();
  });

  it('shows the server refusal of an action in the drawer', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-environment-review-approval');
    await click('deploy-run-approve');
    data.actionResult.error(new HttpErrorResponse({ status: 403, error: { error: 'Approval requires the Owner or Maintainer role.' } }));
    await settle();

    expect(text('deploy-run-action-error')).toBe('Approval requires the Owner or Maintainer role.');
    expect((byTestId('deploy-run-approve') as HTMLButtonElement).disabled).toBe(false);
    expect(data.requests.slice(-3)).toEqual(['status', 'deployments:0:all', 'deployments:0:all']);
  });

  it('confirms abort of a running run and refreshes after a refusal', async () => {
    data.serveEverything();
    const running = deployDeploymentsFixture();
    running[1].state = 'running';
    data.deploymentsPanel.next({ status: 'ready', value: running });
    await settle();
    await click('deploy-tab-history');
    await click('deploy-run-open-run-c-production');
    await click('deploy-run-cancel');

    expect(window.confirm).toHaveBeenCalledWith('Abort Production run run-c-production?');
    expect(data.actions).toEqual(['cancel:run-c-production']);
    data.requests.length = 0;
    data.actionResult.error(new HttpErrorResponse({ status: 403, error: { error: 'Only an approver may abort Production.' } }));
    await settle();
    expect(text('deploy-run-action-error')).toBe('Only an approver may abort Production.');
    expect(data.requests).toEqual(['status', 'deployments:0:all', 'deployments:0:all']);
  });

  it('refreshes status and runs after an already-claimed cancellation conflict', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-environment-review-approval');
    await click('deploy-run-cancel');
    data.requests.length = 0;
    data.actionResult.error(new HttpErrorResponse({ status: 409, error: { error: 'The run is already claimed.' } }));
    await settle();

    expect(text('deploy-run-action-error')).toBe('The run is already claimed.');
    expect(data.requests).toEqual(['status', 'deployments:0:all', 'deployments:0:all']);
  });

  it('keeps an approval in flight when the drawer closes, switches, and Refresh is pressed', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-environment-review-approval');
    await click('deploy-run-approve');
    fixture.componentInstance.closeRun();
    fixture.componentInstance.openRun('run-b-stage');
    expect(fixture.componentInstance.actionState().busy).toBe('approve');
    await click('deploy-refresh');
    expect(data.actionResult.observers).toHaveLength(1);
    data.requests.length = 0;
    data.actionResult.next(deployDeploymentsFixture()[1]);
    await settle();
    expect(fixture.componentInstance.actionState().busy).toBeNull();
    expect(data.requests).toContain('status');
    expect(data.requests).toContain('deployments:0:all');
    expect(data.requests).not.toContain('log:run-c-production');
  });

  it('ignores superseded status and deployment reads after an action result', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-environment-review-approval');
    data.controlledReads = true;
    fixture.componentInstance.refresh();
    await settle();
    const oldStatus = data.statusReads[0];
    const oldRuns = data.deploymentReads.slice(0, 2);
    await click('deploy-run-approve');
    data.actionResult.next(deployDeploymentsFixture()[1]);
    const newStatus = data.statusReads[1];
    const newRuns = data.deploymentReads.slice(2);
    const statuses = deployStatusFixture();
    statuses[1].health = 'unhealthy';
    const runs = deployDeploymentsFixture();
    runs[1].state = 'queued';
    newStatus.next({ status: 'ready', value: statuses });
    for (const read of newRuns) read.next({ status: 'ready', value: { items: runs, offset: 0, limit: 25, total: runs.length } });
    oldStatus.next({ status: 'ready', value: deployStatusFixture() });
    for (const read of oldRuns) read.next({ status: 'ready', value: { items: deployDeploymentsFixture(), offset: 0, limit: 25, total: 7 } });
    await settle();
    expect(fixture.componentInstance.statusPanel()).toEqual({ status: 'ready', value: statuses });
    expect(fixture.componentInstance.selectedRun()?.state).toBe('queued');
  });

  it('requests and renders bounded slices of a large release and deployment history', async () => {
    data.serveEverything();
    const run = deployDeploymentsFixture()[0];
    const release = deployReleasesFixture()[0];
    data.deploymentsPanel.next({ status: 'ready', value: Array.from({ length: 105 }, (_, index) => ({
      ...run, id: `run-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    })) });
    data.releasesPanel.next({ status: 'ready', value: Array.from({ length: 40 }, (_, index) => ({
      ...release, id: `release-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    })) });
    await settle();
    await click('deploy-tab-history');
    expect(host().querySelectorAll('[data-testid^="deploy-run-row-"]')).toHaveLength(25);
    expect(text('deploy-history-count')).toBe('25 of 105 runs');
    await click('deploy-history-next');
    expect(host().querySelectorAll('[data-testid^="deploy-run-row-"]')).toHaveLength(25);
    expect(text('deploy-history-count')).toBe('50 of 105 runs');
    await click('deploy-tab-releases');
    expect(host().querySelectorAll('.releases__row')).toHaveLength(25);
    await click('deploy-releases-next');
    expect(host().querySelectorAll('.releases__row')).toHaveLength(15);
    expect(data.requests).toContain('releases:25');
  });

  it('clears server history filters when leaving History so the reopened controls match the data', async () => {
    data.serveEverything();
    await settle();
    await click('deploy-tab-history');
    const select = byTestId('deploy-history-filter-environment') as HTMLSelectElement;
    select.value = 'production';
    select.dispatchEvent(new Event('change'));
    await settle();
    expect(fixture.componentInstance.historyFilter().environmentId).toBe('production');
    await click('deploy-tab-releases');
    expect(fixture.componentInstance.historyFilter().environmentId).toBe('all');
    await click('deploy-tab-history');
    expect((byTestId('deploy-history-filter-environment') as HTMLSelectElement).value).toBe('all');
    expect(host().querySelectorAll('[data-testid^="deploy-run-row-"]')).toHaveLength(7);
  });

  it('reloads every source on Refresh and keeps the deployment tools reachable', async () => {
    data.requests.length = 0;
    await click('deploy-refresh');
    expect(data.requests).toEqual(['environments:Website', 'releases:0', 'status', 'deployments:0:all', 'deployments:0:all']);

    await click('deploy-open-tools');
    expect(host().querySelector('app-project-deployment-panel')).not.toBeNull();
  });

  it('marks the active tab for assistive technology and keeps P2 tabs disabled', async () => {
    await click('deploy-tab-history');

    expect(byTestId('deploy-tab-history')?.getAttribute('aria-current')).toBe('page');
    expect(byTestId('deploy-tab-overview')?.getAttribute('aria-current')).toBeNull();
    const disabled = Array.from(host().querySelectorAll('.deploy-tabs button:disabled')).map(button => button.textContent?.trim());
    expect(disabled).toEqual(['Variables · P2', 'Runbooks · P2']);
  });
});

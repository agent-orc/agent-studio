import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { DeferBlockState, TestBed, type ComponentFixture } from '@angular/core/testing';
import { BehaviorSubject, Subject, map, of, type Observable } from 'rxjs';
import { vi } from 'vitest';
import { ProjectDeployAreaComponent } from './project-deploy-area';
import { DEPLOY_LIVE_REFRESH_MS } from '../../state/deploy-area-reads';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { ServerCheckoutStore } from '../../../server-checkout';
import { urlProject } from '../../../server-checkout/testing/server-checkout.fixtures';
import type { RegistryProjectSummary } from '../../../../models/task.model';
import { DEPLOY_CLOCK, type DeployActionOption, type DeployPanel } from '../../state/deploy-area.model';
import { AuthSessionState } from '../../../../services/auth.service';
import {
  ARTIFACT_B,
  DEPLOY_FIXTURE_NOW,
  RELEASE_2,
  RELEASE_3,
  deployDeploymentsFixture,
  deployEnvironmentsFixture,
  deployReleasesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  pipelineDefinitionFixture,
  productionRollbackFixture,
  RELEASE_1,
} from '../../testing/deploy-fixtures';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';

class FakeDeployData {
  readonly environmentsPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly statusPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly releasesPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly deploymentsPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly pipelinePanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'loading' });
  readonly healthPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'unavailable' });
  readonly releaseGatePanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'unavailable' });
  readonly requests: string[] = [];
  readonly actions: string[] = [];
  actionResult = new Subject<unknown>();
  controlledReads = false;
  controlledReleases = false;
  controlledEnvironments = false;
  readonly environmentReads: Subject<DeployPanel<unknown>>[] = [];
  readonly releasePageReads: Subject<DeployPanel<unknown>>[] = [];
  readonly containingReads: Subject<DeployPanel<unknown>>[] = [];
  readonly statusReads: Subject<DeployPanel<unknown>>[] = [];
  readonly deploymentReads: Subject<DeployPanel<unknown>>[] = [];
  readonly releaseReads: { project: string; read: Subject<DeployPanel<unknown>> }[] = [];
  readonly artifactLookups: { project: string; artifact: string; notAfterUtc: string | null; read: Subject<DeployPanel<unknown>> }[] = [];
  readonly runsById = new Map<string, unknown>();

  environments(project: string): Observable<DeployPanel<unknown>> {
    this.requests.push(`environments:${project}`);
    if (!this.controlledEnvironments) return this.environmentsPanel;
    const read = new Subject<DeployPanel<unknown>>();
    this.environmentReads.push(read);
    return read;
  }
  statuses(): Observable<DeployPanel<unknown>> {
    this.requests.push('status');
    if (!this.controlledReads) return this.statusPanel;
    const read = new Subject<DeployPanel<unknown>>();
    this.statusReads.push(read);
    return read;
  }
  releases(): Observable<DeployPanel<unknown>> { return this.releasesPanel; }
  releaseNotes(_project: string, id: string): Observable<DeployPanel<unknown>> {
    const release = deployReleasesFixture().find(item => item.id === id);
    return of(release ? { status: 'ready', value: { releaseId: id, version: release.version, preview: true,
      notes: release.cardKeys.map(cardKey => ({ cardKey, text: `Delivered ${cardKey}` })) } } : { status: 'unavailable' });
  }
  deployments(): Observable<DeployPanel<unknown>> { this.requests.push('deployments'); return this.deploymentsPanel; }
  releasePage(project: string, offset: number): Observable<DeployPanel<unknown>> {
    this.requests.push(`releases:${offset}`);
    if (this.controlledReleases) {
      const read = new Subject<DeployPanel<unknown>>();
      this.releasePageReads.push(read);
      this.releaseReads.push({ project, read });
      return read;
    }
    return this.releasesPanel.pipe(map(panel => panel.status === 'ready'
      ? { status: 'ready', value: { items: (panel.value as unknown[]).slice(offset, offset + 25), total: (panel.value as unknown[]).length, offset, limit: 25 } }
      : panel));
  }
  releasePageContaining(_project: string, id: string): Observable<DeployPanel<unknown>> {
    this.requests.push(`release-containing:${id}`);
    if (this.controlledReleases) {
      const read = new Subject<DeployPanel<unknown>>();
      this.containingReads.push(read);
      return read;
    }
    return this.releasesPanel.pipe(map(panel => {
      if (panel.status !== 'ready') return panel;
      const releases = panel.value as { id: string }[];
      const index = releases.findIndex(item => item.id === id);
      if (index < 0) return { status: 'error', message: 'The selected release could not be loaded.' };
      const offset = Math.floor(index / 25) * 25;
      return { status: 'ready', value: { items: releases.slice(offset, offset + 25), total: releases.length, offset, limit: 25 } };
    }));
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
  /** The deployment tools and the Runbooks tab read the shared environment projection on their own. */
  environmentStates(): Observable<DeployPanel<unknown>> { return of({ status: 'loading' }); }
  releaseForArtifact(project: string, artifact: string, notAfterUtc: string | null): Observable<DeployPanel<unknown>> {
    const read = new Subject<DeployPanel<unknown>>();
    this.artifactLookups.push({ project, artifact, notAfterUtc, read });
    return read;
  }
  deployment(_project: string, id: string): Observable<DeployPanel<unknown>> {
    this.requests.push(`deployment:${id}`);
    const run = this.runsById.get(id);
    return new BehaviorSubject<DeployPanel<unknown>>(run ? { status: 'ready', value: run } : { status: 'error', message: `Unknown deployment '${id}'.` });
  }
  pipeline(): Observable<DeployPanel<unknown>> { return this.pipelinePanel; }
  health(): Observable<DeployPanel<unknown>> { return this.healthPanel; }
  integrationRelease(): Observable<DeployPanel<unknown>> { return of({ status: 'unavailable' }); }
  createIntegrationRelease(): Observable<unknown> { return of({}); }
  releaseGate(project: string): Observable<DeployPanel<unknown>> { this.requests.push(`gate:${project}`); return this.releaseGatePanel; }
  readonly secretPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'unavailable' });
  readonly secretRequests: string[] = [];
  secretReadiness(project: string, releaseId?: string | null): Observable<DeployPanel<unknown>> {
    this.secretRequests.push(releaseId ? `${project}:${releaseId}` : project); return this.secretPanel;
  }
  deploymentLog(_project: string, id: string): Observable<DeployPanel<unknown>> {
    this.requests.push(`log:${id}`);
    return new BehaviorSubject<DeployPanel<unknown>>({ status: 'ready', value: deploymentLogFixture(id) });
  }
  readonly actOptions: unknown[] = [];
  act(_project: string, id: string, action: string, _reason?: string, options?: unknown): Observable<unknown> {
    this.actions.push(`${action}:${id}`);
    this.actOptions.push(options);
    return this.actionResult;
  }
  readonly deployActionsPanel = new BehaviorSubject<DeployPanel<unknown>>({ status: 'unavailable' });
  readonly releaseActionReads: string[] = [];
  releaseActionsAnswer: (releaseId: string) => DeployPanel<unknown> = () => ({ status: 'ready', value: [] });
  readonly starts: DeployActionOption[] = [];
  startResult = new Subject<unknown>();
  deployActions(): Observable<DeployPanel<unknown>> { return this.deployActionsPanel; }
  releaseDeployActions(_project: string, releaseId: string): Observable<DeployPanel<unknown>> {
    this.releaseActionReads.push(releaseId);
    return of(this.releaseActionsAnswer(releaseId));
  }
  start(_project: string, option: DeployActionOption): Observable<unknown> {
    this.starts.push(option);
    return this.startResult;
  }
  readonly rollbacks: string[] = [];
  rollbackResult = new Subject<unknown>();
  rollback(_project: string, environmentId: string, body: { approve: boolean; acceptIncidentTarget: boolean }): Observable<unknown> {
    this.rollbacks.push(`${environmentId}:approve=${body.approve}:accept=${body.acceptIncidentTarget}`);
    return this.rollbackResult;
  }

  serveRollback(incidentOnly = false): void {
    const scenario = productionRollbackFixture(incidentOnly);
    this.environmentsPanel.next({ status: 'ready', value: scenario.environments });
    this.statusPanel.next({ status: 'ready', value: scenario.statuses });
    this.releasesPanel.next({ status: 'ready', value: deployReleasesFixture() });
    this.deploymentsPanel.next({ status: 'ready', value: scenario.deployments });
    this.pipelinePanel.next({ status: 'ready', value: pipelineDefinitionFixture() });
  }

  stopEnvironment(_project: string, id: string): Observable<void> { this.actions.push(`stop:${id}`); return of(void 0); }
  confirmRunTargetRemoval(_project: string, id: string, hostId: string): Observable<void> {
    this.actions.push(`remove:${id}:${hostId}`);
    return of(void 0);
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
  const checkoutProject = signal<RegistryProjectSummary | null>(null);
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
    checkoutProject.set(null);
    data = new FakeDeployData();
    await TestBed.configureTestingModule({
      imports: [ProjectDeployAreaComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ProjectDeployDataService, useValue: data },
        { provide: ServerCheckoutStore, useValue: {
          projectFor: () => checkoutProject(), statusFor: () => null,
        } },
        { provide: DEPLOY_CLOCK, useValue: () => DEPLOY_FIXTURE_NOW },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployAreaComponent);
    fixture.componentRef.setInput('projectName', 'Website');
    document.body.appendChild(host());
    await settle();
  });

  afterEach(() => { host().remove(); vi.unstubAllGlobals(); vi.useRealTimers(); });

  it('shows a refused release with its reason and next step until a release clears it (F2)', async () => {
    data.serveEverything();
    await settle();
    expect(data.requests).toContain('gate:Website');
    expect(byTestId('deploy-release-refusal')).toBeNull();

    data.releaseGatePanel.next({ status: 'ready', value: { refusal: {
      commitSha: '86afada0123456789abcdef0123456789abcdef0', mode: 'required',
      reason: 'The candidate has no green pre-main or batch gate on this exact commit.',
      nextStep: 'Release a commit that passed the pre-main or batch gate.', atUtc: '2026-11-16T10:00:00Z',
    } } });
    await settle();
    expect(text('deploy-release-refusal')).toContain('Release refused');
    expect(text('deploy-release-refusal')).toContain('86afada01234 (delivery.fullSuite: required): The candidate has no green pre-main');
    expect(text('deploy-release-refusal-next')).toBe('Next step: Release a commit that passed the pre-main or batch gate.');

    data.releaseGatePanel.next({ status: 'ready', value: { refusal: null } });
    await settle();
    expect(byTestId('deploy-release-refusal')).toBeNull();
  });

  it('shows one checkout state for a URL project and skips deploy reads until its checkout exists', async () => {
    checkoutProject.set(urlProject({ id: 'PROJ-2', displayName: 'Tally Library' }));
    fixture.componentRef.setInput('projectName', 'Tally Library');
    await settle();
    const requests = data.requests.length;
    expect(byTestId('deploy-checkout-missing')?.textContent).toContain('server checkout');
    expect(byTestId('deploy-source-notices')).toBeNull();
    await click('deploy-tab-pipeline');
    await click('deploy-tab-steps');
    await click('deploy-refresh');
    expect(data.requests).toHaveLength(requests);

    checkoutProject.set(urlProject({ id: 'PROJ-2', displayName: 'Tally Library', repositoryPath: '/srv/tally' }));
    await settle();
    expect(byTestId('deploy-checkout-missing')).toBeNull();
    expect(data.requests.length).toBeGreaterThan(requests);
  });

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

  it('confirms stop and queues it without deleting the declaration', async () => {
    data.serveEverything();
    await settle();

    await click('deploy-environment-stop');

    expect(data.actions).toContain('stop:stage');
    expect(vi.mocked(window.confirm).mock.calls[0][0]).toContain('environment secrets are kept');
    expect(text('deploy-environment-action-message')).toContain('Stop queued');
  });

  it('queues a declaration change card and confirms host removal only after it is pending', async () => {
    data.serveEverything();
    await settle();

    await click('deploy-environment-remove');
    expect(vi.mocked(window.confirm).mock.calls[0][0]).toContain('app stays on its host');
    expect(vi.mocked(window.confirm).mock.calls[0][0]).toContain('check-removed');
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/watch-paths').flush([{ name: 'Website', path: '/srv/tasks/website' }]);
    const create = http.expectOne('/api/tasks');
    expect(create.request.body.promptMarkdown).toContain('Do not add a removal tombstone');
    create.flush({ id: 'WEB-100' });
    await settle();
    expect(text('deploy-environment-action-message')).toContain('WEB-100');
    expect(data.actions).not.toContain('remove:stage:run-host');

    const environments: import('../../../../models/project-overview.model').ProjectEnvironments = deployEnvironmentsFixture();
    environments.runTargetRemovals = [{ environmentId: 'stage', hostId: 'run-host', kind: 'stage', status: 'pending-confirmation' }];
    data.environmentsPanel.next({ status: 'ready', value: environments });
    await settle();
    host().querySelector<HTMLButtonElement>('[data-testid="deploy-pending-removal"] button')!.click();
    await settle();
    expect(data.actions).toContain('remove:stage:run-host');
    expect(vi.mocked(window.confirm).mock.calls.at(-1)?.[0]).toContain('environment secrets are kept');
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

  it('opens status-card runs when newer deployments fill the loaded pages', async () => {
    data.serveEverything();
    const other = deployDeploymentsFixture()[0];
    data.deploymentsPanel.next({ status: 'ready', value: Array.from({ length: 30 }, (_, index) => ({
      ...other, id: `run-other-${index}`, environmentId: 'other',
      createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    })) });
    await settle();

    expect(fixture.componentInstance.runs().some(run => run.id === 'run-c-stage-redeploy')).toBe(false);
    expect(fixture.componentInstance.historyRuns().some(run => run.id === 'run-c-production')).toBe(false);

    (host().querySelector('[aria-label="Open last run for Staging"]') as HTMLButtonElement).click();
    await settle();
    expect(text('deploy-run-drawer')).toContain('Redeploy to Staging');
    expect(data.requests).toContain('log:run-c-stage-redeploy');

    await click('sidesheet-close');
    await click('deploy-environment-review-approval');
    expect(text('deploy-run-drawer')).toContain('Deploy to Production');
    expect(byTestId('deploy-run-approve')).not.toBeNull();
    expect(data.requests).toContain('log:run-c-production');
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
    expect(data.requests).toEqual(['environments:Website', 'status', 'deployments:0:all', 'deployments:0:all', 'log:run-c-production']);
    // The table stays while the reload is in flight.
    expect(byTestId('deploy-run-drawer')).not.toBeNull();
  });

  it('re-reads environments after an approval, so the card drops the closed promotion without a reload (F17)', async () => {
    data.serveEverything();
    await settle();
    const productionApproval = () => host().querySelector('[data-testid="deploy-environment-production"] [data-testid="deploy-environment-approval"]');
    expect(productionApproval()).not.toBeNull();
    await click('deploy-environment-review-approval');
    await click('deploy-run-approve');
    expect(window.confirm).toHaveBeenCalledWith('Approve deploying release 2026.10.19.2 (1 card) to Production?');

    // The server queued the run and closed the AGT-3176 promotion.
    const runs = deployDeploymentsFixture();
    runs[1].state = 'queued';
    const statuses = deployStatusFixture();
    statuses[1].currentDeployment = runs[1];
    data.deploymentsPanel.next({ status: 'ready', value: runs });
    data.statusPanel.next({ status: 'ready', value: statuses });
    data.controlledEnvironments = true;
    data.actionResult.next(runs[1]);
    await settle();
    // Run status alone still leaves the stale promotion on the card.
    expect(productionApproval()).not.toBeNull();
    expect(data.environmentReads).toHaveLength(1);
    const environments = deployEnvironmentsFixture();
    environments.promotions[1].status = 'approved';
    data.environmentReads[0].next({ status: 'ready', value: environments });
    await settle();
    expect(productionApproval()).toBeNull();
    expect(text('deploy-run-drawer')).toContain('Queued');
  });

  it('follows a queued run until the host reports, then stops reading (F17)', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    data.serveEverything();
    await settle();
    // Nothing in flight: no reads without a user action.
    data.requests.length = 0;
    vi.advanceTimersByTime(DEPLOY_LIVE_REFRESH_MS * 3);
    expect(data.requests).toEqual([]);

    const runs = deployDeploymentsFixture();
    runs[1].state = 'queued';
    const statuses = deployStatusFixture();
    statuses[1].currentDeployment = runs[1];
    data.deploymentsPanel.next({ status: 'ready', value: runs });
    data.statusPanel.next({ status: 'ready', value: statuses });
    await settle();
    expect(text('deploy-environment-production')).toContain('2026.10.18.1');
    data.requests.length = 0;
    vi.advanceTimersByTime(DEPLOY_LIVE_REFRESH_MS);
    expect(data.requests).toEqual(['environments:Website', 'status', 'deployments:0:all', 'deployments:0:all']);

    // The host reports healthy: the next read shows the new release on the card and in history.
    runs[1].state = 'succeeded';
    runs[1].finishedAtUtc = '2026-10-19T11:59:00Z';
    statuses[1] = { ...statuses[1], currentRelease: RELEASE_3, currentDeployment: runs[1], newerRelease: null, cardsSinceLastDeploy: [] };
    const environments = deployEnvironmentsFixture();
    environments.environments[1].deployedArtifact = environments.environments[0].deployedArtifact;
    environments.environments[1].history.unshift({ at: '2026-10-19T11:59:00Z', status: 'healthy',
      artifact: environments.environments[0].deployedArtifact!, previousArtifact: null, evidence: 'health-ok' });
    environments.promotions[1].status = 'deployed';
    data.environmentsPanel.next({ status: 'ready', value: environments });
    data.statusPanel.next({ status: 'ready', value: statuses });
    data.deploymentsPanel.next({ status: 'ready', value: runs });
    await settle();
    expect(text('deploy-environment-production')).toContain('2026.10.19.2');
    expect(text('deploy-environment-production')).not.toContain('Nothing deployed yet');
    expect(host().querySelector('[data-testid="deploy-environment-production"] [data-testid="deploy-environment-approval"]')).toBeNull();
    data.requests.length = 0;
    vi.advanceTimersByTime(DEPLOY_LIVE_REFRESH_MS * 3);
    expect(data.requests).toEqual([]);
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
    expect(data.requests.slice(-4)).toEqual(['environments:Website', 'status', 'deployments:0:all', 'deployments:0:all']);
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

    expect(window.confirm).toHaveBeenCalledWith('Abort deploying release 2026.10.19.2 (1 card) to Production?');
    expect(data.actions).toEqual(['cancel:run-c-production']);
    data.requests.length = 0;
    data.actionResult.error(new HttpErrorResponse({ status: 403, error: { error: 'Only an approver may abort Production.' } }));
    await settle();
    expect(text('deploy-run-action-error')).toBe('Only an approver may abort Production.');
    expect(data.requests).toEqual(['environments:Website', 'status', 'deployments:0:all', 'deployments:0:all']);
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
    expect(data.requests).toEqual(['environments:Website', 'status', 'deployments:0:all', 'deployments:0:all']);
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

  it('opens a marker release through one bounded containing page', async () => {
    data.serveEverything();
    const release = deployReleasesFixture()[0];
    data.releasesPanel.next({ status: 'ready', value: Array.from({ length: 40 }, (_, index) => ({
      ...release, id: `release-${index}`, version: `version-${index}`,
      createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    })) });
    await settle();
    data.requests.length = 0;

    fixture.componentInstance.openRelease('release-35');
    await settle();

    expect(data.requests).toEqual(['release-containing:release-35']);
    expect(fixture.componentInstance.releaseOffset()).toBe(25);
    expect(host().querySelectorAll('.releases__row')).toHaveLength(15);
    expect(text('deploy-release-detail')).toContain('version-35');
  });

  it('reads only the containing page when the Deploy view mounts with an older release target', async () => {
    data.controlledReleases = true;
    data.requests.length = 0;
    const fresh = TestBed.createComponent(ProjectDeployAreaComponent);
    fresh.componentRef.setInput('projectName', 'Website');
    fresh.componentRef.setInput('releaseTarget', { project: 'Website', id: 'release-35' });
    fresh.detectChanges();
    await fresh.whenStable();

    expect(data.requests.filter(request => request.startsWith('release'))).toEqual(['release-containing:release-35']);
    expect(data.containingReads).toHaveLength(1);
    expect(data.containingReads[0].observers).toHaveLength(1);
    const release = deployReleasesFixture()[0];
    const page = { items: [{ ...release, id: 'release-35', version: 'older release' }], offset: 25, limit: 25, total: 40 };
    data.containingReads[0].next({ status: 'ready', value: page });
    fresh.detectChanges();
    await fresh.whenStable();
    expect(fresh.componentInstance.releaseOffset()).toBe(25);
    expect((fresh.nativeElement as HTMLElement).querySelector('[data-testid="deploy-release-detail"]')?.textContent)
      .toContain('older release');
    fresh.destroy();
  });

  it('opens a new release target on the mounted Deploy view without reloading its other sources', async () => {
    data.serveEverything();
    await settle();
    data.requests.length = 0;
    fixture.componentRef.setInput('releaseTarget', { project: 'Website', id: 'release-35' });
    await settle();
    expect(data.requests).toEqual(['release-containing:release-35']);
    expect(fixture.componentInstance.highlightedReleaseId()).toBe('release-35');
    fixture.componentRef.setInput('releaseTarget', { project: 'Other', id: 'release-9' });
    await settle();
    expect(data.requests).toEqual(['release-containing:release-35']);
  });

  it('does not send a project release target to a second mounted Deploy view', async () => {
    data.controlledReleases = true;
    const targeted = TestBed.createComponent(ProjectDeployAreaComponent);
    targeted.componentRef.setInput('projectName', 'Website');
    targeted.componentRef.setInput('releaseTarget', { project: 'Website', id: 'release-35' });
    targeted.detectChanges();
    await targeted.whenStable();
    const other = TestBed.createComponent(ProjectDeployAreaComponent);
    other.componentRef.setInput('projectName', 'Website');
    other.detectChanges();
    await other.whenStable();
    expect(data.requests.filter(request => request === 'release-containing:release-35')).toHaveLength(1);
    expect(targeted.componentInstance.highlightedReleaseId()).toBe('release-35');
    expect(other.componentInstance.highlightedReleaseId()).toBeNull();
    targeted.destroy();
    other.destroy();
  });

  it('opens an archived marker card by exact key from the Deploy Operations tab', () => {
    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(card => opened.push(card));
    fixture.componentInstance.openCard('CARD-1');
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne(req => req.url === '/api/tasks/CARD-1/core');
    expect(request.request.params.get('project')).toBe('Website');
    request.flush({ id: 'arch-51', key: 'CARD-1', taskKey: 'CARD-1', projectName: 'Website',
      watchPath: '/tasks/website', state: 'ready' });
    expect(opened).toEqual([{ jobId: 'arch-51', watchPath: '/tasks/website' }]);
    http.expectNone(req => req.url === '/api/tasks/archive');
  });

  it('cancels a pending card lookup when the Deploy view is destroyed', () => {
    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(card => opened.push(card));
    fixture.componentInstance.openCard('CARD-1');
    const request = TestBed.inject(HttpTestingController).expectOne(req => req.url === '/api/tasks/CARD-1/core');
    fixture.destroy();
    expect(request.cancelled).toBe(true);
    expect(opened).toEqual([]);
  });

  it('opens and approves a Production run from status that is beyond the first deployment page', async () => {
    data.serveEverything();
    const runs = deployDeploymentsFixture();
    const stage = runs.find(run => run.id === 'run-c-stage')!;
    const newer = Array.from({ length: 30 }, (_, index) => ({
      ...stage, id: `run-stage-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    data.deploymentsPanel.next({ status: 'ready', value: [...newer, ...runs] });
    await settle();
    expect(fixture.componentInstance.runs().some(run => run.id === 'run-c-production')).toBe(false);

    await click('deploy-environment-review-approval');
    expect(text('deploy-run-drawer')).toContain('Production');
    expect(data.requests).not.toContain('deployment:run-c-production');
    await click('deploy-run-approve');
    expect(window.confirm).toHaveBeenCalledWith('Approve deploying release 2026.10.19.2 (1 card) to Production?');
    expect(data.actions).toEqual(['approve:run-c-production']);
  });

  it('lists missing Production secrets on the card and the drawer, binds from there, and confirms the approval with the names (F14)', async () => {
    data.serveEverything();
    data.secretPanel.next({ status: 'ready', value: { project: 'Website', releaseId: null, environments: [
      { environmentId: 'stage', environmentName: 'Stage', kind: 'stage', releaseId: RELEASE_3.id, requiresApproval: false, ready: true, missing: [] },
      { environmentId: 'production', environmentName: 'Production', kind: 'production', releaseId: RELEASE_3.id,
        requiresApproval: true, ready: false,
        missing: [{ name: 'DATABASE_URL', reference: 'secret://Website/production/DATABASE_URL', reason: 'not-bound', bindable: true }] },
    ] } });
    await settle();
    expect(byTestId('deploy-secret-gaps-stage')).toBeNull();
    expect(text('deploy-secret-gaps-production')).toContain('DATABASE_URL');
    expect(text('deploy-secret-gaps-production')).toContain('not bound to this environment');

    await click('deploy-environment-review-approval');
    expect(data.secretRequests).toContain(`Website:${RELEASE_3.id}`);
    expect(text('deploy-run-drawer')).toContain('DATABASE_URL');
    await click('deploy-run-approve');
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('1 secret binding is missing: DATABASE_URL.'));
    expect(data.actions).toEqual(['approve:run-c-production']);
    expect(data.actOptions).toEqual([{ confirmMissingSecrets: true }]);

    fixture.componentInstance.closeRun();
    await settle();
    await click('deploy-secret-bind-production');
    expect(fixture.componentInstance.view()).toBe('secrets');
    expect(fixture.componentInstance.secrets.environment()).toBe('production');
  });

  it('sends no approval when the approver declines the missing-secret confirmation (F14)', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false));
    data.serveEverything();
    data.secretPanel.next({ status: 'ready', value: { project: 'Website', releaseId: RELEASE_3.id, environments: [
      { environmentId: 'production', environmentName: 'Production', kind: 'production', releaseId: RELEASE_3.id,
        requiresApproval: true, ready: false,
        missing: [{ name: 'DATABASE_URL', reference: 'secret://Website/production/DATABASE_URL', reason: 'not-bound', bindable: true }] },
    ] } });
    await settle();
    await click('deploy-environment-review-approval');
    await click('deploy-run-approve');
    expect(data.actions).toEqual([]);
  });

  it('asks a local session without sign-in to create the owner, then approves the Production run', async () => {
    const auth = TestBed.inject(AuthSessionState);
    auth.status.set({ profile: 'local', bootstrapRequired: true, authenticated: false });
    data.serveEverything();
    await settle();
    await click('deploy-environment-review-approval');
    await click('deploy-run-approve');
    expect(data.actions).toEqual([]);
    expect(window.confirm).not.toHaveBeenCalled();
    const dialog = document.querySelector('[data-testid="local-owner-dialog"]') as HTMLElement;
    expect(dialog.querySelector('[data-testid="local-owner-purpose"]')?.textContent).toContain('Production approval needs the owner account.');
    for (const [id, value] of [['local-owner-username', 'local.owner'], ['local-owner-password', 'correct-horse-battery']]) {
      const input = dialog.querySelector<HTMLInputElement>(`[data-testid="${id}"]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    (dialog.querySelector('[data-testid="local-owner-submit"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/studio/auth/bootstrap').flush({
      profile: 'local', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_1', username: 'local.owner', displayName: 'local.owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
    });
    await settle();
    expect(window.confirm).toHaveBeenCalledWith('Approve deploying release 2026.10.19.2 (1 card) to Production?');
    expect(data.actions).toEqual(['approve:run-c-production']);
    expect(document.querySelector('[data-testid="local-owner-dialog"]')).toBeNull();
  });

  it('reads a selected run by id when no page or status contains it, and refreshes it after an action', async () => {
    data.serveEverything();
    const old = { ...deployDeploymentsFixture()[1], id: 'run-old-production', createdAtUtc: '2026-09-01T10:00:00Z' };
    data.runsById.set(old.id, old);
    await settle();
    fixture.componentInstance.openRun(old.id);
    await settle();

    expect(data.requests).toContain(`deployment:${old.id}`);
    expect(fixture.componentInstance.selectedRun()?.id).toBe(old.id);
    data.runsById.set(old.id, { ...old, state: 'succeeded', approvedBy: 'local.owner' });
    data.requests.length = 0;
    await click('deploy-run-approve');
    data.actionResult.next(old);
    await settle();
    expect(data.actions).toEqual([`approve:${old.id}`]);
    expect(data.requests).toContain(`deployment:${old.id}`);
    expect(fixture.componentInstance.selectedRun()?.state).toBe('succeeded');

    fixture.componentInstance.openRun('run-missing');
    await settle();
    expect(byTestId('deploy-run-drawer')).toBeNull();
    expect(text('deploy-source-notices')).toContain("Deploy run: Unknown deployment 'run-missing'.");
  });

  it('resolves a host artifact that status does not name with one bounded lookup', async () => {
    data.serveEverything();
    const environments = deployEnvironmentsFixture();
    environments.environments[1].deployedArtifact = ARTIFACT_B;
    environments.environments[1].history.unshift({ at: '2026-10-19T11:30:00Z', status: 'healthy', artifact: ARTIFACT_B,
      previousArtifact: null, evidence: 'host-migration' });
    data.environmentsPanel.next({ status: 'ready', value: environments });
    data.releasesPanel.next({ status: 'ready', value: [RELEASE_3] });
    await settle();

    expect(data.artifactLookups.map(item => [item.artifact, item.notAfterUtc])).toEqual([[ARTIFACT_B, '2026-10-19T11:30:00Z']]);
    expect(text('deploy-environment-production')).toContain('Looking up the release record');
    data.artifactLookups[0].read.next({ status: 'ready', value: RELEASE_2 });
    await settle();
    expect(text('deploy-environment-production')).toContain(`Release ${RELEASE_2.version}`);
    await click('deploy-refresh');
    expect(data.artifactLookups).toHaveLength(1);
  });

  it('never shows the previous project releases or lookups under a new project, even when they answer late', async () => {
    data.serveEverything();
    data.controlledReleases = true;
    data.releaseReads.length = 0;
    fixture.componentInstance.refresh();
    await settle();
    data.releaseReads[0].read.next({ status: 'ready', value: { items: deployReleasesFixture(), offset: 0, limit: 25, total: 3 } });
    await settle();
    await click('deploy-tab-releases');
    expect(host().querySelectorAll('.releases__row')).toHaveLength(3);
    const environments = deployEnvironmentsFixture();
    environments.environments[1].deployedArtifact = ARTIFACT_B;
    data.environmentsPanel.next({ status: 'ready', value: environments });
    await settle();
    const websiteLookup = data.artifactLookups.at(-1)!;

    fixture.componentInstance.show('overview');
    fixture.componentRef.setInput('projectName', 'Intranet');
    await settle();
    expect(fixture.componentInstance.releasePanel().status).toBe('loading');
    expect(fixture.componentInstance.releaseTotal()).toBe(0);
    // The previous project's reads answer after the switch.
    data.releaseReads[0].read.next({ status: 'ready', value: { items: deployReleasesFixture(), offset: 0, limit: 25, total: 3 } });
    websiteLookup.read.next({ status: 'ready', value: RELEASE_2 });
    await settle();
    expect(fixture.componentInstance.releasePanel().status).toBe('loading');
    expect([...fixture.componentInstance.artifactReleases().values()].map(panel => panel.status)).not.toContain('ready');
    await click('deploy-tab-releases');
    expect(host().querySelectorAll('.releases__row')).toHaveLength(0);
    expect(data.releaseReads.at(-1)!.project).toBe('Intranet');

    data.releaseReads.at(-1)!.read.next({ status: 'ready', value: { items: [RELEASE_3], offset: 0, limit: 25, total: 1 } });
    await settle();
    expect(host().querySelectorAll('.releases__row')).toHaveLength(1);
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

  const option = (environmentId: string, action: 'deploy' | 'redeploy', releaseId: string, extra: Partial<DeployActionOption> = {}): DeployActionOption =>
    ({ environmentId, action, releaseId, releaseVersion: RELEASE_3.version, allowed: true, needsApproval: false, reason: null, ...extra });

  it('offers deploy, approval request and redeploy on the environment cards with the lifecycle refusal reasons', async () => {
    data.serveEverything();
    data.deployActionsPanel.next({ status: 'ready', value: [
      option('stage', 'redeploy', RELEASE_3.id),
      option('production', 'deploy', RELEASE_3.id, { needsApproval: true }),
      option('production', 'redeploy', RELEASE_2.id, { allowed: false,
        reason: 'This release is already queued, running or awaiting approval in Production.' }),
      option('stage', 'deploy', RELEASE_2.id, { allowed: false,
        reason: 'The release is older than the current release in Staging. Use rollback.' }),
    ] });
    await settle();

    expect(text('deploy-action-redeploy-stage')).toBe('Redeploy Staging');
    const request = byTestId('deploy-action-deploy-production') as HTMLButtonElement;
    expect(request.textContent?.trim()).toBe('Request Production approval');
    expect(request.classList).toContain('deploy-actions__primary');
    const refused = byTestId('deploy-action-deploy-stage') as HTMLButtonElement;
    expect(refused.disabled).toBe(true);
    expect(text('deploy-action-deploy-stage-reason')).toBe(
      'Deploy to Staging: The release is older than the current release in Staging. Use rollback.');
    expect(refused.getAttribute('aria-describedby')).toBe(byTestId('deploy-action-deploy-stage-reason')!.id);
    // Production shows its waiting approval; a refusal there would only repeat it.
    expect(byTestId('deploy-action-redeploy-production')).toBeNull();

    // A request needs no confirmation; the started run opens with its log.
    await click('deploy-action-deploy-production');
    expect(window.confirm).not.toHaveBeenCalled();
    expect(data.starts.map(item => `${item.action}:${item.environmentId}:${item.needsApproval}`)).toEqual(['deploy:production:true']);
    expect(request.disabled).toBe(true);
    data.requests.length = 0;
    data.runsById.set('run-requested', { ...deployDeploymentsFixture()[1], id: 'run-requested', state: 'awaiting-approval' });
    data.startResult.next({ id: 'run-requested' });
    await settle();
    expect(data.requests).toContain('status');
    expect(data.requests).toContain('log:run-requested');
    expect(byTestId('deploy-run-drawer')).not.toBeNull();
  });

  it('confirms a direct Production deploy and shows the server refusal next to the buttons', async () => {
    data.serveEverything();
    data.deployActionsPanel.next({ status: 'ready', value: [option('production', 'deploy', RELEASE_3.id)] });
    await settle();

    expect(text('deploy-action-deploy-production')).toBe('Deploy to Production');
    await click('deploy-action-deploy-production');
    expect(window.confirm).toHaveBeenCalledWith('Deploy release 2026.10.19.2 to Production?');
    data.startResult.error(new HttpErrorResponse({ status: 409,
      error: { error: 'Wait until the active deployment in Production has finished.' } }));
    await settle();

    expect(text('deploy-action-error')).toBe('Wait until the active deployment in Production has finished.');
    expect((byTestId('deploy-action-deploy-production') as HTMLButtonElement).disabled).toBe(false);
  });

  it('reads the deploy actions of the shown release on its detail', async () => {
    data.serveEverything();
    data.releaseActionsAnswer = releaseId => ({ status: 'ready', value: releaseId === RELEASE_2.id
      ? [option('stage', 'deploy', RELEASE_2.id, { allowed: false,
        reason: 'The release is older than the current release in Staging. Use rollback.' })]
      : [option('stage', 'redeploy', releaseId), option('production', 'deploy', releaseId, { needsApproval: true })] });
    await settle();
    await click('deploy-tab-releases');

    expect(data.releaseActionReads.at(-1)).toBe(RELEASE_3.id);
    expect(text('deploy-release-actions')).toContain('Redeploy Staging');
    expect(text('deploy-release-actions')).toContain('Request Production approval');

    await click(`deploy-release-${RELEASE_2.id}`);
    expect(data.releaseActionReads.at(-1)).toBe(RELEASE_2.id);
    expect((byTestId('deploy-action-deploy-stage') as HTMLButtonElement).disabled).toBe(true);
    expect(text('deploy-action-deploy-stage-reason')).toBe(
      'Deploy to Staging: The release is older than the current release in Staging. Use rollback.');
  });

  it('reloads every source on Refresh and keeps the deployment tools reachable', async () => {
    data.requests.length = 0;
    await click('deploy-refresh');
    expect(data.requests).toEqual(['environments:Website', 'releases:0', 'gate:Website', 'status', 'deployments:0:all', 'deployments:0:all']);

    await click('deploy-open-tools');
    expect(host().querySelector('app-project-deployment-panel')).not.toBeNull();
  });

  it('marks the active tab for assistive technology without internal placeholders', async () => {
    await click('deploy-tab-history');

    expect(byTestId('deploy-tab-history')?.getAttribute('aria-current')).toBe('page');
    expect(byTestId('deploy-tab-overview')?.getAttribute('aria-current')).toBeNull();
    const disabled = Array.from(host().querySelectorAll('.deploy-tabs button:disabled')).map(button => button.textContent?.trim());
    expect(disabled).toEqual([]);
    const runbooksTab = byTestId('deploy-tab-runbooks') as HTMLButtonElement;
    expect(runbooksTab.disabled).toBe(false);
    await click('deploy-tab-runbooks');
    expect(runbooksTab.getAttribute('aria-current')).toBe('page');
    expect(byTestId('deploy-tab-history')?.getAttribute('aria-current')).toBeNull();
    expect(host().querySelector('app-project-environment-overview')).not.toBeNull();
    expect(host().textContent).toContain('Runbooks and their history by environment.');
  });

  describe('rollback (F13)', () => {
    const offerIn = (scope: string) => host().querySelector(
      `[data-testid="${scope}"] [data-testid="rollback-offer-button"]`) as HTMLButtonElement | null;

    it('offers the rollback on the Production card with target, age and runtime health, and asks before it sends', async () => {
      data.serveRollback();
      await settle();
      const confirm = vi.spyOn(TestBed.inject(ConfirmDialogService), 'confirm').mockResolvedValue(false);
      expect(host().querySelector('[data-testid="deploy-environment-production"] [data-testid="rollback-offer-target"]')?.textContent?.trim())
        .toBe(`Release ${RELEASE_1.version}`);
      expect(text('deploy-environment-production')).toContain('Ran without incident');
      expect(text('deploy-environment-production')).toContain('Skips Release 2026.10.19.1: incident while running.');

      offerIn('deploy-environment-production')!.click();
      await settle();
      expect(confirm).toHaveBeenCalledWith(expect.objectContaining({
        title: `Roll back Production to Release ${RELEASE_1.version}?`,
        items: expect.arrayContaining(['Current: Release 2026.10.19.2']),
      }));
      expect(data.rollbacks).toEqual([]);

      confirm.mockResolvedValue(true);
      offerIn('deploy-environment-production')!.click();
      await settle();
      expect(data.rollbacks).toEqual(['production:approve=true:accept=false']);
      expect(window.confirm).not.toHaveBeenCalled();
      data.runsById.set('run-rollback', { ...productionRollbackFixture().deployments[0], id: 'run-rollback',
        releaseId: RELEASE_1.id, trigger: 'rollback', state: 'queued', finishedAtUtc: null });
      data.rollbackResult.next({ id: 'run-rollback' });
      await settle();
      expect(byTestId('deploy-run-drawer')).not.toBeNull();
    });

    it('sends the incident acceptance only for a target that had an incident, and shows a refusal', async () => {
      data.serveRollback(true);
      await settle();
      vi.spyOn(TestBed.inject(ConfirmDialogService), 'confirm').mockResolvedValue(true);
      expect(text('deploy-environment-production')).toContain('Had an incident while running');
      offerIn('deploy-environment-production')!.click();
      await settle();
      expect(data.rollbacks).toEqual(['production:approve=true:accept=true']);
      data.rollbackResult.error(new HttpErrorResponse({ status: 403, error: { error: 'Production approval requires Owner or Maintainer.' } }));
      await settle();
      expect(text('deploy-rollback-error')).toBe(
        'Production: Production approval requires Owner or Maintainer. Redeploy a known release or fix forward.');
    });

    it('offers the same rollback in the run drawer of the current release only', async () => {
      data.serveRollback();
      await settle();
      vi.spyOn(TestBed.inject(ConfirmDialogService), 'confirm').mockResolvedValue(true);
      fixture.componentInstance.openRun('run-c-production-done');
      await settle();
      expect(text('deploy-run-rollback')).toContain(`Release ${RELEASE_1.version}`);
      offerIn('deploy-run-rollback')!.click();
      await settle();
      expect(data.rollbacks).toEqual(['production:approve=true:accept=false']);

      fixture.componentInstance.openRun('run-a-stage');
      await settle();
      expect(byTestId('deploy-run-rollback')).toBeNull();
    });
  });
});


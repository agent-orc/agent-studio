import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideZonelessChangeDetection } from '@angular/core';
import { DeferBlockState, TestBed, type ComponentFixture } from '@angular/core/testing';
import { BehaviorSubject, Subject, type Observable } from 'rxjs';
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

  environments(project: string): Observable<DeployPanel<unknown>> { this.requests.push(`environments:${project}`); return this.environmentsPanel; }
  statuses(): Observable<DeployPanel<unknown>> { this.requests.push('status'); return this.statusPanel; }
  releases(): Observable<DeployPanel<unknown>> { return this.releasesPanel; }
  deployments(): Observable<DeployPanel<unknown>> { this.requests.push('deployments'); return this.deploymentsPanel; }
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
    expect(data.requests).toEqual(['status', 'deployments', 'log:run-c-production']);
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
    expect(data.requests.slice(-2)).toEqual(['status', 'deployments']);
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
    expect(data.requests).toEqual(['status', 'deployments']);
  });

  it('reloads every source on Refresh and keeps the deployment tools reachable', async () => {
    data.requests.length = 0;
    await click('deploy-refresh');
    expect(data.requests).toEqual(['environments:Website', 'status', 'deployments']);

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

import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProjectDeploymentPanelComponent } from './project-deployment-panel.component';
import { DEPLOY_CLOCK } from '../../state/deploy-area.model';
import {
  DEPLOY_FIXTURE_NOW,
  RELEASE_1,
  deployEnvironmentsFixture,
  deployStatusFixture,
  productionDownHealthFixture,
} from '../../testing/deploy-fixtures';

/** The deploy status and control-plane health reads of the shared environment projection; 404 means "not on this server". */
function answerStateSources(http: HttpTestingController, project: string, status?: unknown, health?: unknown): void {
  const statusRead = http.expectOne(`/api/projects/${project}/deploy/status`);
  if (status === undefined) statusRead.flush('', { status: 404, statusText: 'Not Found' });
  else statusRead.flush(status);
  http.expectOne(`/api/projects/${project}/environments/health`).flush(health ?? { capturedAt: '', probeOrigin: 'test', environments: [] });
}

describe('ProjectDeploymentPanelComponent', () => {
  it('shows the server rollback refusal and recovery choices', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/environments').flush({ project: 'Demo', environments: [], promotions: [], failureCards: [] });
    answerStateSources(http, 'Demo', []);
    http.expectOne('/api/watch-paths').flush([]);
    http.expectOne('/api/projects/Demo/deployment/summary').flush({
      project: 'Demo', available: false, reason: null, source: '', lastDeployment: null,
      history: [], pendingCount: null, pendingCommits: [], targets: [], defaultEvidenceRun: null,
    });
    fixture.componentInstance.rollbackEnvironment('production');
    http.expectOne('/api/projects/Demo/environments/production/rollback').flush(
      { error: 'No previous artifact is available.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('No previous artifact is available.');
    expect(alert.textContent).toContain('Redeploy');
    expect(alert.textContent).toContain('fix forward');
  });
  it('renders the shared baseline, pending delta, and newest-first history', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo%20Project/environments').flush({
      project: 'Demo Project', environments: [{
        declaration: { id: 'stage', name: 'Stage', kind: 'stage', targetHostId: 'web-stage',
          address: 'https://stage.example', template: 'container', destination: '8081', containerPort: 8080,
          healthUrl: 'https://stage.example/health', approval: 'person', secretRefs: [] },
        deployedArtifact: 'app@sha256:' + 'a'.repeat(64), health: 'healthy', pendingArtifact: null, history: [],
      }],
      promotions: [{ artifact: 'app@sha256:' + 'a'.repeat(64), stageId: 'stage', productionId: 'production',
        stageHealthyAt: '2026-10-16T12:00:00Z', status: 'pending', actor: null, expiresAt: '2026-10-17T12:00:00Z' }],
      failureCards: [],
    });
    answerStateSources(http, 'Demo%20Project', [{
      environment: { id: 'stage' }, currentRelease: { version: '2026.10.16.3', artifactDigests: ['sha256:' + 'a'.repeat(64)] },
      currentDeployment: null, newerRelease: null, cardsSinceLastDeploy: [], health: 'healthy', releaseUncertain: false,
    }]);
    http.expectOne('/api/watch-paths').flush([{ name: 'Demo Project', path: 'C:/projects/demo' }]);
    http
      .expectOne('/api/projects/Demo%20Project/deployment/summary')
      .flush({
        project: 'Demo Project', available: true, reason: null, stableBaseline: true,
        source: 'logs/stable-restarts.jsonl', pendingCount: 2,
        defaultEvidenceRun: { id: 'TR-green', commit: 'bbbbbbbb', branch: 'develop', scope: { level: 'project', testSet: 'all' }, completedAt: '2026-07-11T08:00:00Z', distanceToHead: 2, headDirection: 'head-ahead' },
        pendingCommits: [{ sha: 'cccccccc', shortSha: 'cccccccc', subject: 'Pending', authorDateUtc: '2026-07-11T10:00:00Z' }],
        lastDeployment: { at: '2026-07-11T09:00:00Z', status: 'ok', headBefore: 'aaaaaaaa', headAfter: 'bbbbbbbb', durationSeconds: 42, jobsSinceLastRestart: 3, reviewCountAfter: 4, commits: [] },
        history: [
          { at: '2026-07-11T09:00:00Z', status: 'ok', headBefore: 'aaaaaaaa', headAfter: 'bbbbbbbb', durationSeconds: 42, jobsSinceLastRestart: 3, reviewCountAfter: 4, commits: [] },
          { at: '2026-07-10T09:00:00Z', status: 'failed', headBefore: '11111111', headAfter: '22222222', durationSeconds: 7, jobsSinceLastRestart: 1, reviewCountAfter: 2, commits: [] },
        ],
        targets: [{ id: 'deploy-stable', title: 'deploy-stable', kind: 'derived', template: 'deploy-stable', summary: 'Deploy stable.', runnable: true, source: 'repository-fact', command: 'bash scripts/supervisor/restart-stable-after-batch.sh', targetHostId: null, parameters: [{ name: 'stableIdle', type: 'boolean', required: true, default: false, options: [] }] }],
      });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-pending-count"]').textContent.trim()).toBe('2');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="project-deployment-history"] > li').length).toBe(2);
    const runTones = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="project-deployment-run-status"]'))
      .map(item => (item as HTMLElement).dataset['tone']);
    expect(runTones).toEqual(['success', 'warning']);
    const targetStatus = fixture.nativeElement.querySelector('[data-testid="project-deployment-target-status"]') as HTMLElement;
    expect(targetStatus.textContent?.trim()).toBe('Idle required');
    expect(targetStatus.dataset['tone']).toBe('warning');
    expect(fixture.nativeElement.textContent).toContain('deploy-stable');
    expect(fixture.nativeElement.textContent).not.toContain('Run deployment');
    expect(fixture.nativeElement.textContent).toContain('Head is 2 commits ahead');
    expect(fixture.nativeElement.textContent).toContain('Promotion pending');
    expect(fixture.nativeElement.textContent).toContain('Release 2026.10.16.3');
    fixture.nativeElement.querySelector('[data-testid="pending-promotion"] button').click();
    const approval = http.expectOne('/api/projects/Demo%20Project/environments/promotions/approve');
    expect(approval.request.body).toEqual({ artifact: 'app@sha256:' + 'a'.repeat(64) });
    approval.flush({});
    http.expectOne('/api/projects/Demo%20Project/environments').flush({ project: 'Demo Project', environments: [], promotions: [], failureCards: [] });
    answerStateSources(http, 'Demo%20Project', []);

    fixture.nativeElement.querySelector('[data-testid="deployment-visible-task"]').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Visible task execution is required for this workflow.');
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-run"]').getAttribute('aria-describedby'))
      .toBe('deployment-visible-task-required');
    fixture.nativeElement.querySelector('[data-testid="deployment-visible-task"]').click();
    fixture.nativeElement.querySelector('[data-testid="deployment-param-stableIdle"]').click();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="project-deployment-run"]').click();
    const create = http.expectOne('/api/tasks');
    expect(create.request.body.promptMarkdown).toContain('deploymentTarget: deploy-stable');
    expect(create.request.body.promptMarkdown).toContain('testRunId: TR-green');
    expect(create.request.body.promptMarkdown).toContain('deploymentCommit: bbbbbbbb');
    expect(create.request.body.promptMarkdown).toContain('headDirection: head-ahead');
    create.flush({ id: 'AGT-3000' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-created"]').textContent).toContain('AGT-3000');
  });

  it('accepts false as a provided value for ordinary required yes-no parameters', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo%20Project/environments')
      .flush({ project: 'Demo Project', environments: [], promotions: [], failureCards: [] });
    answerStateSources(TestBed.inject(HttpTestingController), 'Demo%20Project', []);
    const target = {
      id: 'docs', title: 'Docs', kind: 'template' as const, template: 'caddy-site', summary: 'Deploy docs.',
      runnable: true, source: 'deployment.json', command: 'bash scripts/deploy.sh --reload {{reload}}', targetHostId: 'web',
      parameters: [{ name: 'reload', type: 'boolean' as const, required: true, default: false, options: [] }],
    };

    fixture.componentInstance.chooseTarget(target);

    expect(fixture.componentInstance.canRun(target)).toBe(false);
    fixture.componentInstance.headExceptionReason.set('Emergency repair approved by operator');
    expect(fixture.componentInstance.canRun(target)).toBe(true);
  });

  it('explains project-specific unavailable history', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Other');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Other/environments')
      .flush({ project: 'Other', environments: [], promotions: [], failureCards: [] });
    answerStateSources(http, 'Other', []);
    http.expectOne('/api/watch-paths').flush([]);
    http.expectOne('/api/projects/Other/deployment/summary').flush({
      project: 'Other', available: false, reason: 'Latest deploy-stable revision range does not belong to this project repository.',
      source: 'logs/stable-restarts.jsonl', lastDeployment: null, history: [], pendingCount: null, pendingCommits: [], targets: [],
      defaultEvidenceRun: null, stableBaseline: true,
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-unavailable"]').textContent)
      .toContain('does not belong to this project repository');
  });

  it('M2: shows Production down from the open incident and the release version, not the last report and raw digest', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: DEPLOY_CLOCK, useValue: () => DEPLOY_FIXTURE_NOW }],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Website');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    // Every deploy report still says healthy.
    http.expectOne('/api/projects/Website/environments').flush(deployEnvironmentsFixture());
    answerStateSources(http, 'Website', deployStatusFixture(), productionDownHealthFixture());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const production = fixture.nativeElement.querySelector('[data-testid="environment-production"]') as HTMLElement;
    const health = production.querySelector('[data-testid="environment-state-health"]') as HTMLElement;
    expect(health.textContent?.trim()).toBe('Down');
    expect(health.dataset['tone']).toBe('danger');
    expect(production.querySelector('[data-testid="environment-state-release"]')?.textContent?.replace(/\s+/g, ' ').trim())
      .toBe(`Release ${RELEASE_1.version} deployed 13 h ago`);
    expect(production.querySelector('[data-testid="environment-state-digest"]')?.textContent?.trim()).toBe('website@sha256:aaaaaaaaaaaa');
    expect(production.querySelector('[data-testid="environment-health-state"]')).toBeNull();
    const staging = fixture.nativeElement.querySelector('[data-testid="environment-stage"] [data-testid="environment-state-health"]') as HTMLElement;
    expect(staging.textContent?.trim()).toBe('Up');
  });

  it('leaves Studio\'s stable baseline out and points a schema 2 project to Deploy', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeploymentPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeploymentPanelComponent);
    fixture.componentRef.setInput('projectName', 'Shop');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Shop/environments')
      .flush({ project: 'Shop', environments: [], promotions: [], failureCards: [] });
    http.expectOne('/api/watch-paths').flush([]);
    http.expectOne('/api/projects/Shop/deployment/summary').flush({
      project: 'Shop', available: false, reason: 'The deploy-stable baseline is Studio\'s own deployment history.',
      source: 'logs/stable-restarts.jsonl', lastDeployment: null, history: [], pendingCount: null, pendingCommits: [], targets: [],
      defaultEvidenceRun: null, stableBaseline: false, pipelineDeploySteps: 2,
    });
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toContain('stable-restarts.jsonl');
    expect(text).not.toContain('Stable baseline');
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-no-targets"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="project-deployment-pipeline-steps"]').textContent)
      .toContain('2 deploy steps in project.yml');
  });
});

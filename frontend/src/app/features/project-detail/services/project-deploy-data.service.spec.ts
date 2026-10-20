import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectDeployDataService } from './project-deploy-data.service';
import type { DeployPanel } from '../state/deploy-area.model';
import {
  deployDeploymentsFixture,
  deployReleasesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  pipelineDefinitionFixture,
} from '../testing/deploy-fixtures';

/** The AGT-3201 routes as DeployEndpoints.cs maps them. */
const DEPLOY = '/api/projects/Web%20Site/deploy';
const MISSING_ROUTE = { status: 404, statusText: 'Not Found' };

describe('ProjectDeployDataService', () => {
  let http: HttpTestingController;
  let service: ProjectDeployDataService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ProjectDeployDataService);
  });

  function collect<T>(source: { subscribe(next: (value: DeployPanel<T>) => void): unknown }): DeployPanel<T>[] {
    const values: DeployPanel<T>[] = [];
    source.subscribe(value => values.push(value));
    return values;
  }

  afterEach(() => http.verify());

  it('reads status, releases, deployments, and a step log from the AGT-3201 routes', () => {
    const status = collect(service.statuses('Web Site'));
    const releases = collect(service.releases('Web Site'));
    const deployments = collect(service.deployments('Web Site'));
    const log = collect(service.deploymentLog('Web Site', 'run-b-stage'));

    http.expectOne(`${DEPLOY}/status`).flush(deployStatusFixture());
    http.expectOne(`${DEPLOY}/releases`).flush(deployReleasesFixture());
    http.expectOne(`${DEPLOY}/deployments`).flush(deployDeploymentsFixture());
    http.expectOne(`${DEPLOY}/deployments/run-b-stage/log`).flush({ deploymentId: 'run-b-stage', lines: deploymentLogFixture('run-b-stage') });

    expect(status).toEqual([{ status: 'loading' }, { status: 'ready', value: deployStatusFixture() }]);
    expect(releases.at(-1)).toEqual({ status: 'ready', value: deployReleasesFixture() });
    expect(deployments.at(-1)).toEqual({ status: 'ready', value: deployDeploymentsFixture() });
    expect(log.at(-1)).toEqual({ status: 'ready', value: deploymentLogFixture('run-b-stage') });
  });

  it('reports AGT-3201 routes as unavailable only when the route itself is missing', () => {
    const releases = collect(service.releases('Web Site'));
    const deployments = collect(service.deployments('Web Site'));

    http.expectOne(`${DEPLOY}/releases`).flush(null, MISSING_ROUTE);
    http.expectOne(`${DEPLOY}/deployments`).flush({ error: "Unknown project 'Web Site'" }, MISSING_ROUTE);

    expect(releases.at(-1)).toEqual({ status: 'unavailable' });
    expect(deployments.at(-1)).toEqual({ status: 'error', message: "Unknown project 'Web Site'" });
  });

  it('never masks a server failure as unavailable', () => {
    const status = collect(service.statuses('Web Site'));
    const releases = collect(service.releases('Web Site'));

    http.expectOne(`${DEPLOY}/status`).flush('boom', { status: 500, statusText: 'Server Error' });
    http.expectOne(`${DEPLOY}/releases`).flush({ error: 'invalid deployment state' }, { status: 500, statusText: 'Server Error' });

    expect(status.at(-1)).toEqual({ status: 'error', message: 'Environment status could not be loaded.' });
    expect(releases.at(-1)).toEqual({ status: 'error', message: 'invalid deployment state' });
  });

  it('posts approve, reject, and cancel to the AGT-3201 deployment routes', () => {
    for (const action of ['approve', 'reject', 'cancel'] as const) {
      let answered = false;
      service.act('Web Site', 'run-c-production', action).subscribe(() => (answered = true));
      const request = http.expectOne(`${DEPLOY}/deployments/run-c-production/${action}`);
      expect(request.request.method).toBe('POST');
      request.flush(deployDeploymentsFixture()[1]);
      expect(answered).toBe(true);
    }
  });

  it('treats a missing environments project as an error, not as unavailable', () => {
    const values = collect(service.environments('web'));

    http.expectOne('/api/projects/web/environments').flush(null, MISSING_ROUTE);
    expect(values.at(-1)).toEqual({ status: 'error', message: 'Environments could not be loaded.' });
  });

  it('draws the pipeline from the AGT-3221 pipeline definition when the server has it', () => {
    const values = collect(service.pipeline('Web Site'));

    http.expectOne('/api/projects/Web%20Site/pipeline-definition').flush(pipelineDefinitionFixture());
    http.expectNone('/api/projects/Web%20Site/execution');
    const last = values.at(-1);
    expect(last?.status).toBe('ready');
    expect(last?.status === 'ready' && last.value.diagram?.environments[0].steps[0].kind).toBe('static-site');
  });

  it('reports a missing pipeline-definition route without inventing a lifecycle', () => {
    const values = collect(service.pipeline('Web Site'));

    http.expectOne('/api/projects/Web%20Site/pipeline-definition').flush(null, MISSING_ROUTE);
    http.expectNone('/api/projects/Web%20Site/execution');
    expect(values.at(-1)).toEqual({ status: 'unavailable' });
  });

  it('does not fall back when the pipeline definition answers with an error', () => {
    const values = collect(service.pipeline('web'));

    http.expectOne('/api/projects/web/pipeline-definition').flush({ error: 'Project not found.', code: 'not-found' }, MISSING_ROUTE);
    http.expectNone('/api/projects/web/execution');
    expect(values.at(-1)).toEqual({ status: 'error', message: 'Project not found.' });
  });
});

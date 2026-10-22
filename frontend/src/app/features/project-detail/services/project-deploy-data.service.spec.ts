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

  it('requests bounded release and filtered deployment pages', () => {
    const releases = collect(service.releasePage('Web Site', 25));
    const runs = collect(service.deploymentPage('Web Site', 50,
      { environmentId: 'production', state: 'failed', period: '7d' }, Date.parse('2026-10-19T12:00:00Z')));
    http.expectOne(`${DEPLOY}/releases/page?offset=25&limit=25`).flush({
      items: deployReleasesFixture().slice(1), offset: 25, limit: 25, total: 28,
    });
    const request = http.expectOne(req => req.url === `${DEPLOY}/deployments/page`);
    expect(request.request.params.get('offset')).toBe('50');
    expect(request.request.params.get('limit')).toBe('25');
    expect(request.request.params.get('environmentId')).toBe('production');
    expect(request.request.params.get('state')).toBe('failed');
    expect(request.request.params.get('sinceUtc')).toBe('2026-10-12T12:00:00.000Z');
    request.flush({ items: deployDeploymentsFixture().slice(0, 1), offset: 50, limit: 25, total: 51 });
    expect(releases.at(-1)).toMatchObject({ status: 'ready', value: { total: 28 } });
    expect(runs.at(-1)).toMatchObject({ status: 'ready', value: { total: 51 } });
  });

  it('resolves a host artifact to its release with one bounded read, and reads one run by id', () => {
    const [release] = deployReleasesFixture();
    const found = collect(service.releaseForArtifact('Web Site', 'registry.example/website@sha256:b', '2026-10-19T11:57:00Z'));
    const none = collect(service.releaseForArtifact('Web Site', 'legacy@sha256:x', null));
    const run = collect(service.deployment('Web Site', 'run-b-stage'));

    const lookup = http.expectOne(req => req.url === `${DEPLOY}/releases/page` && req.params.get('artifact') === 'registry.example/website@sha256:b');
    expect(lookup.request.params.get('limit')).toBe('1');
    expect(lookup.request.params.get('notAfterUtc')).toBe('2026-10-19T11:57:00Z');
    lookup.flush({ items: [release], offset: 0, limit: 1, total: 2 });
    const legacy = http.expectOne(req => req.url === `${DEPLOY}/releases/page` && req.params.get('artifact') === 'legacy@sha256:x');
    expect(legacy.request.params.has('notAfterUtc')).toBe(false);
    legacy.flush({ items: [], offset: 0, limit: 1, total: 0 });
    http.expectOne(`${DEPLOY}/deployments/run-b-stage`).flush(deployDeploymentsFixture()[0]);

    expect(found.at(-1)).toEqual({ status: 'ready', value: release });
    expect(none.at(-1)).toEqual({ status: 'ready', value: null });
    expect(run.at(-1)).toEqual({ status: 'ready', value: deployDeploymentsFixture()[0] });
  });

  it('reads AGT-3223 environment health and distinguishes unavailable from server error', () => {
    const health = collect(service.health('Web Site'));
    const response = { capturedAt: '2026-10-19T12:00:00Z', probeOrigin: 'mock', environments: [] };
    http.expectOne('/api/projects/Web%20Site/environments/health').flush(response);
    expect(health.at(-1)).toEqual({ status: 'ready', value: response });

    const unavailable = collect(service.health('Web Site'));
    http.expectOne('/api/projects/Web%20Site/environments/health').flush(null, MISSING_ROUTE);
    expect(unavailable.at(-1)).toEqual({ status: 'unavailable' });

    const error = collect(service.health('Web Site'));
    http.expectOne('/api/projects/Web%20Site/environments/health').flush({ error: 'Probe store failed.' },
      { status: 500, statusText: 'Server Error' });
    expect(error.at(-1)).toEqual({ status: 'error', message: 'Probe store failed.' });
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

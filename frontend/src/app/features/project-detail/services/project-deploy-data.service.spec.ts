import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ProjectDeployDataService } from './project-deploy-data.service';
import { DEPLOY_HEALTH_CHECKS, type DeployPanel } from '../state/deploy-area.model';
import { deployHealthFixture, deployReleasesFixture } from '../testing/deploy-fixtures';

describe('ProjectDeployDataService', () => {
  let http: HttpTestingController;

  function setup(healthChecks: unknown = null): ProjectDeployDataService {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DEPLOY_HEALTH_CHECKS, useValue: healthChecks },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    return TestBed.inject(ProjectDeployDataService);
  }

  function collect<T>(source: { subscribe(next: (value: DeployPanel<T>) => void): unknown }): DeployPanel<T>[] {
    const values: DeployPanel<T>[] = [];
    source.subscribe(value => values.push(value));
    return values;
  }

  afterEach(() => http.verify());

  it('reports releases as unavailable when the release route answers 404', () => {
    const values = collect(setup().releases('Web Site'));

    http.expectOne('/api/projects/Web%20Site/releases').flush({}, { status: 404, statusText: 'Not Found' });
    expect(values).toEqual([{ status: 'loading' }, { status: 'unavailable' }]);
  });

  it('reports other release failures as errors', () => {
    const values = collect(setup().releases('web'));

    http.expectOne('/api/projects/web/releases').flush({}, { status: 500, statusText: 'Server Error' });
    expect(values.at(-1)).toEqual({ status: 'error', message: 'Releases could not be loaded.' });
  });

  it('accepts a bare release array and a wrapped one', () => {
    const service = setup();
    const bare = collect(service.releases('web'));
    http.expectOne('/api/projects/web/releases').flush(deployReleasesFixture());
    const wrapped = collect(service.releases('web'));
    http.expectOne('/api/projects/web/releases').flush({ releases: deployReleasesFixture() });

    expect(bare.at(-1)).toEqual({ status: 'ready', value: deployReleasesFixture() });
    expect(wrapped.at(-1)).toEqual({ status: 'ready', value: deployReleasesFixture() });
  });

  it('treats a missing environments project as an error, not as unavailable', () => {
    const values = collect(setup().environments('web'));

    http.expectOne('/api/projects/web/environments').flush({}, { status: 404, statusText: 'Not Found' });
    expect(values.at(-1)).toEqual({ status: 'error', message: 'Environments could not be loaded.' });
  });

  it('reads the lifecycle from the execution definition', () => {
    const values = collect(setup().lifecycle('web'));

    http.expectOne('/api/projects/web/execution').flush({ valid: true, lifecycle: { declared: false, nodes: [] } });
    expect(values.at(-1)).toEqual({ status: 'ready', value: { valid: true, lifecycle: { declared: false, nodes: [] } } });
  });

  it('reports health checks as unavailable without a health-check source, and makes no request', () => {
    expect(collect(setup().healthSummaries('web'))).toEqual([{ status: 'unavailable' }]);
  });

  it('reads health summaries from a provided source', () => {
    const values = collect(setup({ summaries: () => of(deployHealthFixture()) }).healthSummaries('web'));

    expect(values.at(-1)).toEqual({ status: 'ready', value: deployHealthFixture() });
  });
});

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { InstanceDeployDataService } from './instance-deploy-data.service';
import { instanceDeployFixture } from '../testing/instance-deploy-fixtures';
import type { DeployPanel } from '../state/deploy-area.model';
import type { InstanceDeployView } from '../state/instance-deploy.model';

describe('InstanceDeployDataService', () => {
  let http: HttpTestingController;
  let service: InstanceDeployDataService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(InstanceDeployDataService);
  });

  afterEach(() => http.verify());

  function read(answer: (url: string) => void): DeployPanel<InstanceDeployView>[] {
    const panels: DeployPanel<InstanceDeployView>[] = [];
    service.instance('Agent Studio').subscribe(panel => panels.push(panel));
    answer('/api/projects/Agent%20Studio/deploy/instance');
    return panels;
  }

  it('answers the instance view of the source project', () => {
    const panels = read(url => http.expectOne(url).flush(instanceDeployFixture()));
    expect(panels.map(panel => panel.status)).toEqual(['loading', 'ready']);
  });

  it('renders nothing for another project, a missing route, or a body that is not an instance view', () => {
    expect(read(url => http.expectOne(url).flush({ error: 'not the source' }, { status: 404, statusText: 'Not Found' })).at(-1))
      .toEqual({ status: 'unavailable' });
    expect(read(url => http.expectOne(url).flush([])).at(-1)).toEqual({ status: 'unavailable' });
  });

  it('names a server failure with its message', () => {
    const last = read(url => http.expectOne(url).flush({ error: 'Deploy records could not be read.' },
      { status: 500, statusText: 'Server Error' })).at(-1);
    expect(last).toEqual({ status: 'error', message: 'Deploy records could not be read.' });
  });
});

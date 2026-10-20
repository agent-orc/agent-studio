import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TaskService } from '../../../../services/task.service';
import { ProjectOperationsRailComponent } from './project-operations-rail';

describe('ProjectOperationsRailComponent', () => {
  it('opens an archived card linked from a deploy marker', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectOperationsRailComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    TestBed.inject(TaskService).jobs.set([]);
    const fixture = TestBed.createComponent(ProjectOperationsRailComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(item => opened.push(item));
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/demo/deploy/operations').flush({
      project: 'demo', since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
      environments: [{ environment: { id: 'prod', name: 'Production', kind: 'production' },
        telemetryAvailable: false, metrics: null, markers: [{ deploymentId: 'run', releaseId: 'release',
          version: '1', atUtc: '2026-10-20T00:00:00Z', trigger: 'deploy', state: 'succeeded', cardKeys: ['CARD-1'] }] }],
      dora: { successfulProductionDeploys: 1, deploysPerWeek: 0.25, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: 0, medianRecoveryHours: null, recoveriesMeasured: 0 },
    });
    await fixture.whenStable();
    const releases: string[] = [];
    fixture.componentInstance.releaseSelected.subscribe(id => releases.push(id));
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="operations-release-release"]')!.click();
    expect(releases).toEqual(['release']);
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.project-operations__cards button')!.click();
    const archive = http.expectOne(request => request.url === '/api/tasks/CARD-1/core');
    expect(archive.request.params.get('project')).toBe('demo');
    archive.flush({ id: 'arch-1', key: 'CARD-1', taskKey: 'CARD-1', projectName: 'demo', watchPath: '/tasks/demo', state: 'ready' });
    expect(opened).toEqual([{ jobId: 'arch-1', watchPath: '/tasks/demo' }]);
  });

  it('resolves an exact archived key beyond fifty fuzzy archive matches', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectOperationsRailComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    TestBed.inject(TaskService).jobs.set([]);
    const fixture = TestBed.createComponent(ProjectOperationsRailComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/demo/deploy/operations').flush({ project: 'demo', environments: [], dora: {} });
    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(item => opened.push(item));
    fixture.componentInstance.openCard('CARD-1');
    const request = http.expectOne(req => req.url === '/api/tasks/CARD-1/core');
    request.flush({ id: 'arch-51', key: 'CARD-1', taskKey: 'CARD-1', projectName: 'demo', watchPath: '/tasks/demo', state: 'ready' });
    expect(opened).toEqual([{ jobId: 'arch-51', watchPath: '/tasks/demo' }]);
    http.expectNone(req => req.url === '/api/tasks/archive');
  });
});

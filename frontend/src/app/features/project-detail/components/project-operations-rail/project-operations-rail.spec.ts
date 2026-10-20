import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TaskService } from '../../../../services/task.service';
import { ProjectOperationsRailComponent } from './project-operations-rail';
import { OperationsReleaseSelectionService } from '../../state/operations-release-selection.service';

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
    let releaseSelected = 0;
    fixture.componentInstance.releaseSelected.subscribe(() => releaseSelected++);
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="operations-release-release"]')!.click();
    expect(TestBed.inject(OperationsReleaseSelectionService).releaseId()).toBe('release');
    expect(releaseSelected).toBe(1);
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.project-operations__cards button')!.click();
    const archive = http.expectOne(request => request.url === '/api/tasks/archive');
    expect(archive.request.params.get('project')).toBe('demo');
    expect(archive.request.params.get('search')).toBe('CARD-1');
    archive.flush({ items: [{ id: 'arch-1', key: 'CARD-1', watchPath: '/tasks/demo' }], total: 1, offset: 0, limit: 50 });
    expect(opened).toEqual([{ jobId: 'arch-1', watchPath: '/tasks/demo' }]);
  });
});

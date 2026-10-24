import { computed, signal, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Subject } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TaskService } from '../../../services/task.service';
import type { TaskInfo } from '../../../models/task.model';
import { CardDeploymentService } from './card-deployment.service';

describe('CardDeploymentService refresh', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('loads badges when observed, then refreshes after task events and deployment polling', () => {
    vi.useFakeTimers();
    const task = { projectName: 'demo', key: 'DEMO-1', id: 'demo-1' } as TaskInfo;
    const jobs = signal<TaskInfo[]>([task]);
    const taskEvents = new Subject<unknown>();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: TaskService, useValue: { jobs, taskEvents } }],
    });
    const service = TestBed.inject(CardDeploymentService);
    const http = TestBed.inject(HttpTestingController);
    http.expectNone('/api/deploy/cards');
    taskEvents.next({ kind: 'mutated', id: task.id });
    http.expectNone('/api/deploy/cards');
    const badges = computed(() => service.badges(task));
    expect(badges()).toEqual([]);
    http.expectNone('/api/deploy/cards');
    service.observe();
    const first = http.expectOne('/api/deploy/cards');
    taskEvents.next({ kind: 'mutated', id: task.id });
    http.expectNone('/api/deploy/cards');
    first.flush([{
      projectId: 'demo', cardKey: 'DEMO-1', environments: [
        { environmentId: 'stage', environmentName: 'Staging', kind: 'staging', state: 'succeeded',
          releaseId: 'r1', releaseVersion: '1', atUtc: '2026-10-16T12:00:00Z' },
      ],
    }]);
    expect(service.inEnvironment(task, 'staging')).toBe(true);

    const second = http.expectOne('/api/deploy/cards');
    second.flush([]);
    expect(service.badges(task)).toEqual([]);

    vi.advanceTimersByTime(30_000);
    const deploymentRefresh = http.expectOne('/api/deploy/cards');
    deploymentRefresh.flush([{
      projectId: 'demo', cardKey: 'DEMO-1', environments: [
        { environmentId: 'production', environmentName: 'Production', kind: 'production', state: 'succeeded',
          releaseId: 'r2', releaseVersion: '2', atUtc: '2026-10-16T13:00:00Z' },
      ],
    }]);
    expect(service.inEnvironment(task, 'production')).toBe(true);
    http.verify();
    taskEvents.complete();
  });

  it('starts loading after tasks arrive when a badge is observed first', () => {
    const task = { projectName: 'demo', key: 'DEMO-1', id: 'demo-1' } as TaskInfo;
    const jobs = signal<TaskInfo[]>([]);
    const taskEvents = new Subject<unknown>();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: TaskService, useValue: { jobs, taskEvents } }],
    });
    const service = TestBed.inject(CardDeploymentService);
    const http = TestBed.inject(HttpTestingController);
    service.observe();
    http.expectNone('/api/deploy/cards');
    jobs.set([task]);
    taskEvents.next({ kind: 'snapshot' });
    http.expectOne('/api/deploy/cards').flush([]);
    http.verify();
    taskEvents.complete();
  });
});

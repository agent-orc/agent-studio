import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { EMPTY, NEVER, Subject, of } from 'rxjs';
import type { CreateTaskRequest } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { CliCatalogStore } from '../../cli';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { QuotaApiService } from '../../quota';
import { CreateTaskFormService } from './create-task-form.service';

describe('CreateTaskFormService', () => {
  it.each([
    { explicit: false, expected: false },
    { explicit: true, expected: true },
  ])('submits visible prefilled values with explicit=$expected', ({ explicit, expected }) => {
    const createJob = vi.fn((request: CreateTaskRequest) => {
      expect(request.title).toBe('Draft');
      return of({ id: 'created' });
    });
    TestBed.configureTestingModule({
      providers: [
        CreateTaskFormService,
        { provide: TaskService, useValue: { createJob } },
        { provide: CliCatalogStore, useValue: {} },
        { provide: ErrorDialogService, useValue: {} },
        { provide: QuotaApiService, useValue: {} },
      ],
    });
    const form = TestBed.inject(CreateTaskFormService);
    form.newTitle = 'Draft';
    form.newModel.set('prefilled-model');
    form.newThinkingLevel.set('high');
    form.modelSelectionExplicit.set(explicit);

    form.submit();

    const request = createJob.mock.calls[0][0] as CreateTaskRequest;
    expect(request.model).toBe('prefilled-model');
    expect(request.thinkingLevel).toBe('high');
    expect(request.modelExplicit).toBe(expected);
    expect(request.thinkingLevelExplicit).toBe(expected);
  });

  it('pins a project model default and clears that pin after a CLI change', () => {
    TestBed.configureTestingModule({
      providers: [
        CreateTaskFormService,
        { provide: TaskService, useValue: { getRegistryProjects: () => of([
          { storageLocation: '/workspace/demo', modelDefault: 'project-model' },
        ]) } },
        { provide: CliCatalogStore, useValue: { hasFresh: () => true, modelsFor: () => [] } },
        { provide: ErrorDialogService, useValue: {} },
        { provide: QuotaApiService, useValue: {
          getModelRoutingRecommendation: () => of({ model: 'routed-model', thinkingLevel: 'medium' }),
        } },
      ],
    });
    const form = TestBed.inject(CreateTaskFormService);
    form.visible.set(true);
    form.selectProject('/workspace/demo');
    expect(form.newModel()).toBe('project-model');
    expect(form.modelSelectionExplicit()).toBe(true);

    form.onCreateCliTypeChange(form.newCliType() === 'codex' ? 'claude' : 'codex');
    expect(form.modelSelectionExplicit()).toBe(false);
  });

  it('settles defaults when a registry response arrives during open', () => {
    TestBed.configureTestingModule({ providers: [
      CreateTaskFormService,
      { provide: TaskService, useValue: { getRegistryProjects: () => of([
        { storageLocation: '/workspace/demo', modelDefault: 'project-model' },
      ]) } },
      { provide: CliCatalogStore, useValue: { hasFresh: () => true, modelsFor: () => [] } },
      { provide: ErrorDialogService, useValue: {} },
      { provide: QuotaApiService, useValue: { getModelRoutingRecommendation: () => of({ model: 'policy-model' }) } },
    ] });
    const form = TestBed.inject(CreateTaskFormService);
    form.open({
      watchPaths: [{ name: 'Demo', path: '/workspace/demo', rootPath: '/workspace/demo' }],
      activeProjects: new Set(['Demo']),
    });
    expect(form.defaultsPending()).toBe(false);
    expect(form.newModel()).toBe('project-model');
  });

  it('enables Create when a limited session suppresses the policy suggestion read', () => {
    TestBed.configureTestingModule({ providers: [
      CreateTaskFormService,
      { provide: TaskService, useValue: { getRegistryProjects: () => of([
        { storageLocation: '/workspace/demo', modelDefault: 'project-model' },
      ]) } },
      { provide: CliCatalogStore, useValue: { hasFresh: () => true, modelsFor: () => [] } },
      { provide: ErrorDialogService, useValue: {} },
      { provide: QuotaApiService, useValue: { getModelRoutingRecommendation: () => EMPTY } },
    ] });
    const form = TestBed.inject(CreateTaskFormService);
    form.open({
      watchPaths: [{ name: 'Demo', path: '/workspace/demo', rootPath: '/workspace/demo' }],
      activeProjects: new Set(['Demo']),
    });
    expect(form.defaultsPending()).toBe(false);
    expect(form.suggestionPending()).toBe(false);
    expect(form.agentPending()).toBe(false);
    expect(form.newModel()).toBe('project-model');
  });

  it.each([EMPTY, of([])])('reports an empty project-default response with a retry path', response => {
    TestBed.configureTestingModule({ providers: [
      CreateTaskFormService,
      { provide: TaskService, useValue: { getRegistryProjects: () => response } },
      { provide: CliCatalogStore, useValue: { hasFresh: () => true, modelsFor: () => [] } },
      { provide: ErrorDialogService, useValue: {} },
      { provide: QuotaApiService, useValue: { getModelRoutingRecommendation: () => of({ model: 'policy-model' }) } },
    ] });
    const form = TestBed.inject(CreateTaskFormService);
    form.open({
      watchPaths: [{ name: 'Demo', path: '/workspace/demo', rootPath: '/workspace/demo' }],
      activeProjects: new Set(['Demo']),
    });
    expect(form.defaultsPending()).toBe(false);
    expect(form.defaultsError()).toMatch(/project model defaults.*retry/i);
  });

  it('bounds a stalled project-default lookup, explains the failure, and retries', async () => {
    vi.useFakeTimers();
    try {
      const getRegistryProjects = vi.fn()
        .mockReturnValueOnce(NEVER)
        .mockReturnValueOnce(of([{ storageLocation: '/workspace/demo', modelDefault: 'recovered-model' }]));
      TestBed.configureTestingModule({ providers: [
        CreateTaskFormService,
        { provide: TaskService, useValue: { getRegistryProjects } },
        { provide: CliCatalogStore, useValue: { hasFresh: () => true, modelsFor: () => [] } },
        { provide: ErrorDialogService, useValue: {} },
        { provide: QuotaApiService, useValue: { getModelRoutingRecommendation: () => of({ model: 'policy-model' }) } },
      ] });
      const form = TestBed.inject(CreateTaskFormService);
      form.visible.set(true);
      form.selectProject('/workspace/demo');
      expect(form.agentPending()).toBe(true);

      await vi.advanceTimersByTimeAsync(5_100);
      expect(form.defaultsPending()).toBe(false);
      expect(form.defaultsError()).toMatch(/timed out.*retry/i);

      form.retryProjectDefaults();
      expect(getRegistryProjects).toHaveBeenCalledTimes(2);
      expect(form.defaultsError()).toBeNull();
      expect(form.newModel()).toBe('recovered-model');
      expect(form.agentPending()).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('ignores an old project-default response after switching projects', () => {
    const first = new Subject<{ storageLocation: string; modelDefault: string }[]>();
    const getRegistryProjects = vi.fn()
      .mockReturnValueOnce(first)
      .mockReturnValueOnce(of([{ storageLocation: '/workspace/second', modelDefault: 'second-model' }]));
    TestBed.configureTestingModule({ providers: [
      CreateTaskFormService,
      { provide: TaskService, useValue: { getRegistryProjects } },
      { provide: CliCatalogStore, useValue: { hasFresh: () => true, modelsFor: () => [] } },
      { provide: ErrorDialogService, useValue: {} },
      { provide: QuotaApiService, useValue: { getModelRoutingRecommendation: () => of({ model: 'policy-model' }) } },
    ] });
    const form = TestBed.inject(CreateTaskFormService);
    form.visible.set(true);
    form.selectProject('/workspace/first');
    form.selectProject('/workspace/second');
    first.next([{ storageLocation: '/workspace/first', modelDefault: 'first-model' }]);
    expect(form.newModel()).toBe('second-model');
    expect(form.defaultsError()).toBeNull();
  });
});

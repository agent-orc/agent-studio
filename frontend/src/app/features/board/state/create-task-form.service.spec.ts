import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
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
    form.newModel = 'prefilled-model';
    form.newThinkingLevel = 'high';
    form.modelSelectionExplicit = explicit;

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
    expect(form.newModel).toBe('project-model');
    expect(form.modelSelectionExplicit).toBe(true);

    form.onCreateCliTypeChange(form.newCliType === 'codex' ? 'claude' : 'codex');
    expect(form.modelSelectionExplicit).toBe(false);
  });
});

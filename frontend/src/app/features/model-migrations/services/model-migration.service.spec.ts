import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ModelMigrationService, applyRequest } from './model-migration.service';
import type { ModelMigrationProposal, ModelMigrationSnapshot } from '../models/model-migration.model';

const proposal: ModelMigrationProposal = {
  scope: 'task',
  projectName: 'Agent Studio',
  taskId: 'AGT-2692',
  fromModel: 'claude-sonnet-4-6',
  toModel: 'claude-sonnet-5',
  rule: 'same-family-current',
  catalogVersion: '2026-09-06',
  explicit: true,
  costClassFrom: 'standard',
  costClassTo: 'standard',
  reasoningLadderFrom: ['low', 'medium', 'high'],
  reasoningLadderTo: ['low', 'medium', 'high'],
};

function snapshot(proposals: ModelMigrationProposal[] = [proposal]): ModelMigrationSnapshot {
  return {
    catalogVersion: '2026-09-06',
    catalogSource: 'Token Economy',
    autoApplySafe: true,
    proposals,
  };
}

describe('ModelMigrationService', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    return {
      service: TestBed.inject(ModelMigrationService),
      http: TestBed.inject(HttpTestingController),
    };
  }

  it('shares one workspace request and filters pinned task proposals', () => {
    const { service, http } = setup();
    service.ensureWorkspaceLoaded();
    service.ensureWorkspaceLoaded();

    const request = http.expectOne('/api/model-migrations');
    request.flush(snapshot());

    expect(service.proposalForTask('AGT-2692', 'Agent Studio', 'claude-sonnet-4-6'))
      .toEqual(proposal);
    expect(service.proposalForTask('AGT-2692', 'Agent Studio', 'claude-sonnet-5')).toBeNull();
    http.verify();
  });

  it('scopes pipeline proposals by project, pipeline type, and step', () => {
    const { service, http } = setup();
    const pipeline = {
      ...proposal,
      scope: 'pipelineStep' as const,
      taskId: null,
      pipelineType: 'feature',
      stepId: 'aspect-code-quality',
    };
    service.ensureProjectLoaded('Agent Studio');
    const request = http.expectOne((candidate) =>
      candidate.url === '/api/model-migrations'
      && candidate.params.get('project') === 'Agent Studio');
    request.flush(snapshot([pipeline]));

    expect(service.proposalForPipelineStep(
      'Agent Studio', 'feature', 'aspect-code-quality', 'claude-sonnet-4-6',
    )).toEqual(pipeline);
    expect(service.proposalForPipelineStep(
      'Agent Studio', 'task', 'aspect-code-quality', 'claude-sonnet-4-6',
    )).toBeNull();
    http.verify();
  });

  it('sends compare-and-set identity only and removes an applied proposal', () => {
    const { service, http } = setup();
    service.ensureWorkspaceLoaded();
    http.expectOne('/api/model-migrations').flush(snapshot());

    let completed = false;
    service.apply(proposal).subscribe(() => { completed = true; });
    expect(service.isApplying(proposal)).toBe(true);
    const request = http.expectOne('/api/model-migrations/apply');
    expect(request.request.body).toEqual(applyRequest(proposal));
    expect(request.request.body).not.toHaveProperty('toModel');
    expect(request.request.body).not.toHaveProperty('rule');
    request.flush({});

    expect(completed).toBe(true);
    expect(service.isApplying(proposal)).toBe(false);
    expect(service.proposalForTask('AGT-2692', 'Agent Studio')).toBeNull();

    const refreshes = http.match((candidate) => candidate.url === '/api/model-migrations');
    expect(refreshes).toHaveLength(2);
    for (const refresh of refreshes) refresh.flush(snapshot([]));
    http.verify();
  });

  it('persists the workspace automatic-application switch', () => {
    const { service, http } = setup();
    service.ensureWorkspaceLoaded();
    http.expectOne('/api/model-migrations').flush(snapshot());

    service.setAutoApply(false).subscribe();
    const request = http.expectOne('/api/model-migrations/auto-apply');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enabled: false });
    request.flush({ autoApplySafe: false });

    expect(service.workspace()?.autoApplySafe).toBe(false);
    http.verify();
  });
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PrepareWorkbenchDecisionRequest } from '../../../models/project-docs.model';
import { WorkbenchDecisionStore } from './workbench-decision.store';

const PREPARE: PrepareWorkbenchDecisionRequest = {
  operationId: 'workbench-ui-store-test',
  outcome: 'rework',
  expectedRevision: 'a'.repeat(40),
  expectedFingerprint: 'b'.repeat(64),
  actor: 'Operator',
  archiveReason: null,
  task: null,
  responses: [{
    decisionId: 'layout',
    kind: 'single',
    selectedOptionIds: [],
    comment: 'Move the total higher.',
  }],
};

describe('WorkbenchDecisionStore', () => {
  let store: WorkbenchDecisionStore;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    store = TestBed.inject(WorkbenchDecisionStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('uses the shared prepare and confirm endpoints for a created-card receipt', () => {
    const feature = {
      ...PREPARE,
      outcome: 'feature-spawn' as const,
      task: {
        title: 'Implement layout', goal: 'Use option A.', acceptanceCriteria: ['Ship it.'],
        evidenceLinks: [], chosenOption: 'A', relatedTaskKeys: [], targetProject: 'Demo',
        initialLane: '2-ready' as const, mode: 'coding' as const, taskType: 'feature' as const,
      },
      responses: [{ ...PREPARE.responses[0], selectedOptionIds: ['a'] }],
    };
    store.prepare('Demo', 'layout', feature).subscribe();
    http.expectOne('/api/projects/Demo/workbenches/layout/decisions/prepare').flush(result('prepared'));

    store.confirm('Demo', 'layout', {
      ...feature,
      cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high',
      spawnedTaskKeys: ['DEM-42'], confirmed: true,
    }).subscribe();
    const confirm = http.expectOne('/api/projects/Demo/workbenches/layout/decisions/confirm');
    expect(confirm.request.body).toEqual(expect.objectContaining({
      cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high', spawnedTaskKeys: ['DEM-42'],
    }));
    confirm.flush(result('succeeded'));
    expect(store.state('Demo', 'layout').result?.decisionStage).toBe('succeeded');
  });

  it('hands the rework request and its round prompt to confirm without an orchestrator turn', () => {
    let round: unknown = null;
    store.requestRework(
      'Agent Studio', 'layout', { ...PREPARE, request: 'Add a privacy section.' },
      'Revise the Dossier.\nAdd a privacy section.',
      { cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high' },
    ).subscribe(confirmed => round = confirmed.revisionRound);

    http.expectOne('/api/projects/Agent%20Studio/workbenches/layout/decisions/prepare')
      .flush({ ...result('prepared'), revision: 'c'.repeat(40) });
    const confirm = http.expectOne(
      '/api/projects/Agent%20Studio/workbenches/layout/decisions/confirm');
    expect(confirm.request.body).toEqual(expect.objectContaining({
      outcome: 'rework', expectedRevision: 'c'.repeat(40), request: 'Add a privacy section.',
      revisionPrompt: 'Revise the Dossier.\nAdd a privacy section.',
      cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high', confirmed: true,
    }));
    const queued = {
      status: 'queued', taskKey: 'AGT-2300', title: 'Concept', queueReason: 'project-busy',
      detail: 'Another card holds the project\'s runner slot.',
    };
    confirm.flush({ ...result('pending'), revisionRound: queued });
    expect(round).toEqual(queued);
    http.expectNone(request => request.url.includes('/orchestrator/sessions/'));
  });
});

function result(stage: 'prepared' | 'pending' | 'succeeded') {
  return {
    success: true,
    errorCode: null,
    error: null,
    workbenchId: 'layout',
    operationId: PREPARE.operationId,
    outcome: PREPARE.outcome,
    decisionStage: stage,
    revision: 'a'.repeat(40),
    fingerprint: 'b'.repeat(64),
    spawnedTaskKeys: [],
    responses: PREPARE.responses,
    idempotent: false,
  };
}

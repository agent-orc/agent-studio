import { beforeEach, describe, expect, it } from 'vitest';
import { WorkbenchDecisionDraftStore } from './workbench-decision-draft.store';

describe('WorkbenchDecisionDraftStore', () => {
  beforeEach(() => { sessionStorage.clear(); localStorage.clear(); });

  it('restores fields and selected options after the tab closes (localStorage, AGT-3875)', () => {
    const first = new WorkbenchDecisionDraftStore();
    first.saveResponses('Agent Studio', 'naming-dossier', [{
      decisionId: 'naming',
      kind: 'single',
      selectedOptionIds: ['stable-key'],
      comment: 'Keep the public key stable.',
    }]);
    first.beginFeature('Agent Studio', 'naming-dossier', {
      actor: 'Operator',
      title: 'Apply the naming decision',
      goal: 'Use the selected public naming contract.',
      operationId: 'workbench-ui-persisted',
    }, first.draft('Agent Studio', 'naming-dossier')!.responses);

    const restored = new WorkbenchDecisionDraftStore()
      .draft('Agent Studio', 'naming-dossier');

    expect(restored).toEqual(expect.objectContaining({
      mode: 'feature-spawn',
      title: 'Apply the naming decision',
      goal: 'Use the selected public naming contract.',
      operationId: 'workbench-ui-persisted',
      responses: [expect.objectContaining({ selectedOptionIds: ['stable-key'] })],
    }));
  });

  it('removes the persisted draft explicitly', () => {
    const drafts = new WorkbenchDecisionDraftStore();
    drafts.saveResponses('Agent Studio', 'naming-dossier', [{
      decisionId: 'naming',
      kind: 'single',
      selectedOptionIds: ['stable-key'],
      comment: null,
    }]);

    drafts.discard('Agent Studio', 'naming-dossier');

    expect(new WorkbenchDecisionDraftStore().draft('Agent Studio', 'naming-dossier')).toBeNull();
  });

  it('keeps drafts in localStorage and moves an older sessionStorage draft over once', () => {
    sessionStorage.setItem('atp.workbench-decision-drafts.v1', JSON.stringify({ version: 1, entries: {
      'Agent%20Studio:naming-dossier': {
        mode: null, actor: 'Operator', title: '', goal: '', operationId: null, createdCard: null,
        responses: [{ decisionId: 'naming', kind: 'single', selectedOptionIds: ['stable-key'], comment: 'Note' }],
        updatedAt: '2026-11-16T10:00:00Z',
      },
    } }));

    const migrated = new WorkbenchDecisionDraftStore().draft('Agent Studio', 'naming-dossier');

    expect(migrated?.responses[0].comment).toBe('Note');
    expect(sessionStorage.getItem('atp.workbench-decision-drafts.v1')).toBeNull();
    expect(localStorage.getItem('atp.workbench-decision-drafts.v1')).toContain('stable-key');
  });

  it('warns about leaving once per unsaved answer set', () => {
    const drafts = new WorkbenchDecisionDraftStore();
    const answer = { decisionId: 'naming', kind: 'single' as const, selectedOptionIds: ['stable-key'], comment: null };
    drafts.saveResponses('Agent Studio', 'naming-dossier', [answer]);
    drafts.markLeaveWarned('Agent Studio', 'naming-dossier');
    expect(new WorkbenchDecisionDraftStore().draft('Agent Studio', 'naming-dossier')?.leaveWarned).toBe(true);

    drafts.saveResponses('Agent Studio', 'naming-dossier', [{ ...answer, selectedOptionIds: ['other'] }]);
    expect(drafts.draft('Agent Studio', 'naming-dossier')?.leaveWarned).toBe(false);
  });
});

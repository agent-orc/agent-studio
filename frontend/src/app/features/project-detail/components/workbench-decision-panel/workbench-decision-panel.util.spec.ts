import { describe, expect, it } from 'vitest';
import {
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
} from '../../../../models/project-docs.model';
import { carryRecordedSelections, reworkPrompt, revisionRoundText } from './workbench-decision-panel.util';

const POINTS: WorkbenchDecisionPoint[] = [
  {
    id: 'd3', kind: 'single', label: 'Privacy', commentLabel: null,
    options: [{ id: 'cloud', label: 'Cloud sync' }, { id: 'local', label: 'Local only' }],
  },
  {
    id: 'd4', kind: 'multi', label: 'Formats', commentLabel: null,
    options: [{ id: 'md', label: 'Markdown' }, { id: 'pdf', label: 'PDF' }],
  },
  {
    id: 'd5', kind: 'single', label: 'New point', commentLabel: null,
    options: [{ id: 'yes', label: 'Yes' }, { id: 'no', label: 'No' }],
  },
];

const AUTHORED: WorkbenchDecisionResponse[] = [
  { decisionId: 'd3', kind: 'single', selectedOptionIds: ['cloud'], comment: null },
  { decisionId: 'd4', kind: 'multi', selectedOptionIds: ['md'], comment: null },
  { decisionId: 'd5', kind: 'single', selectedOptionIds: ['yes'], comment: null },
];

describe('carryRecordedSelections', () => {
  it('shows the owner choices instead of the authored recommendations after a revision', () => {
    const carried = carryRecordedSelections([
      { decisionId: 'd3', kind: 'single', selectedOptionIds: ['local'], comment: 'Add a privacy section.' },
      { decisionId: 'd4', kind: 'multi', selectedOptionIds: ['md', 'pdf'], comment: null },
    ], POINTS, AUTHORED);

    expect(carried).toEqual([
      { decisionId: 'd3', kind: 'single', selectedOptionIds: ['local'], comment: null },
      { decisionId: 'd4', kind: 'multi', selectedOptionIds: ['md', 'pdf'], comment: null },
      AUTHORED[2],
    ]);
  });

  it('keeps the authored default where the revision dropped the option or changed the kind', () => {
    const carried = carryRecordedSelections([
      { decisionId: 'd3', kind: 'single', selectedOptionIds: ['removed'], comment: null },
      { decisionId: 'd4', kind: 'single', selectedOptionIds: ['pdf'], comment: null },
      { decisionId: 'd5', kind: 'single', selectedOptionIds: [], comment: 'Only a note.' },
    ], POINTS, AUTHORED);

    expect(carried).toEqual(AUTHORED);
    expect(carryRecordedSelections(null, POINTS, AUTHORED)).toEqual(AUTHORED);
  });
});

describe('reworkPrompt', () => {
  it('leads with the owner request and asks the round to keep the recorded selections', () => {
    const document = {
      workbench: { id: 'notes-export', key: 'AGT-W60', title: 'Notes export',
        entryPath: 'docs/concepts/notes-export/index.html' },
    } as WorkbenchDocument;

    const prompt = reworkPrompt(document, POINTS, [AUTHORED[0]], '  Add a privacy section. ');

    expect(prompt).toContain('Revise Dossier AGT-W60: Notes export.');
    expect(prompt).toContain('Owner request:\nAdd a privacy section.');
    expect(prompt).toContain('Owner selections:\nPrivacy: Cloud sync');
    expect(prompt).toContain('keep the pending rework receipt');
    expect(prompt).not.toContain('remove the pending rework receipt');
  });
});

describe('revisionRoundText', () => {
  it('says whether the round started, queued with its reason, or failed', () => {
    const base = { taskKey: 'AGT-3649', title: 'Concept', queueReason: null, detail: null };
    expect(revisionRoundText({ ...base, status: 'started' }))
      .toBe('Revision round started on AGT-3649 · Concept.');
    expect(revisionRoundText({ ...base, status: 'queued', queueReason: 'project-busy',
      detail: 'Another card holds the slot.' }))
      .toBe('Revision round queued on AGT-3649 · Concept. Another card holds the slot.');
    expect(revisionRoundText({ ...base, status: 'failed', detail: 'No runner configured.' }))
      .toContain('could not start: No runner configured.');
  });
});

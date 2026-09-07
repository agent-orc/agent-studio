import { describe, expect, it } from 'vitest';
import { TaskState } from '../../../../../models/task.model';
import { laneName, laneSentence, laneTone } from '../../../../../models/lane-presentation';
import { deriveProtocolVerdict, type ProtocolVerdictInputs } from '../../protocol-pane/protocol-verdict';
import { outcomeDecisionBadge } from './outcome-decision-badge.util';

/**
 * AGT-2715 — the reported screenshot, asserted end to end.
 *
 * A card parked in `5-human-review` used to be described four different ways
 * on one screen: "Review" on the header chip, "Human review lane" on the
 * Result tab header, "Human review" on the decision badge (a normaliser
 * rewrote the previous string), and "Awaiting human review." in the project
 * workflow section. This spec pins the chain that produced three of those —
 * lane signal -> verdict -> badge -> tooltip — to a single word.
 *
 * The board-header / chip / Result-header rendering is covered end to end by
 * `e2e/task-detail/lane-presentation-one-source.spec.ts`.
 */
function inputs(laneState: string): ProtocolVerdictInputs {
  return {
    isRunning: false,
    summaryStatus: 'ready',
    statusMarkdown: null,
    outcomeIssue: null,
    hasActivity: true,
    laneState,
  };
}

describe('lane wording agrees across the run-outcome chain', () => {
  it('names the Human review lane signal with the lane presentation', () => {
    const verdict = deriveProtocolVerdict(inputs(TaskState.HumanReview));

    expect(verdict.label).toBe(laneName(TaskState.HumanReview));
    expect(verdict.label).toBe('Human review');
    expect(verdict.detail).toBe(laneSentence(TaskState.HumanReview));
  });

  it('carries the lane through so the Result header can wear the lane tone', () => {
    const verdict = deriveProtocolVerdict(inputs(TaskState.HumanReview));

    expect(verdict.lane).toBe(TaskState.HumanReview);
    expect(laneTone(verdict.lane!)).toBe('human-review');
  });

  it('leaves `lane` null when the leading signal is not the lane', () => {
    const verdict = deriveProtocolVerdict({
      ...inputs(TaskState.HumanReview),
      // A failure outranks the lane's needs-decision signal, so the header
      // must fall back to the generic problem tone rather than a lane tone.
      statusMarkdown: '# Status\n- Result: Failed\n',
    });

    expect(verdict.status).toBe('failed');
    expect(verdict.lane).toBeNull();
  });

  it('renders the badge and its tooltip with the same word, unrewritten', () => {
    const verdict = deriveProtocolVerdict(inputs(TaskState.HumanReview));
    const badge = outcomeDecisionBadge(verdict)!;

    expect(badge.label).toBe('Human review');
    // The old normaliser fixed the badge but not the tooltip, which kept
    // saying "Run outcome: Human review lane".
    expect(badge.tooltip.title).toBe('Run outcome: Human review');
    expect(badge.tooltip.title).not.toContain('lane');
  });

  it('names the Escalated and Delivered lane signals the same way', () => {
    expect(deriveProtocolVerdict(inputs(TaskState.Escalated)).label)
      .toBe(laneName(TaskState.Escalated));
    expect(deriveProtocolVerdict(inputs(TaskState.Completed)).label)
      .toBe(laneName(TaskState.Completed));
  });

  it('no longer leaks a raw lane key into the Completed detail copy', () => {
    const verdict = deriveProtocolVerdict(inputs(TaskState.Completed));

    expect(verdict.detail).toBe(laneSentence(TaskState.Completed));
    expect(verdict.detail).not.toContain('6-completed');
  });
});

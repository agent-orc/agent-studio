import { describe, expect, it } from 'vitest';
import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  ALL_LANE_NAMES,
  LANE_PRESENTATIONS,
  laneDocTopic,
  laneGlyph,
  laneName,
  lanePresentation,
  laneSentence,
  laneTone,
} from './lane-presentation';

/**
 * AGT-2715. The reported defect was one lane wearing four names and two tones
 * across the app. These tests lock the properties that make that impossible:
 * complete coverage of the lane keys, uniqueness of name and tone, and the
 * specific screenshot case the operator raised.
 */
describe('lane presentation', () => {
  it('covers every canonical lane state exactly once', () => {
    const catalogued = LANE_PRESENTATIONS.map((lane) => lane.state);
    expect([...catalogued].sort()).toEqual([...ALL_TASK_STATES].sort());
    expect(new Set(catalogued).size).toBe(catalogued.length);
  });

  it('gives every lane a name, a sentence, a tone and a glyph', () => {
    for (const lane of LANE_PRESENTATIONS) {
      expect(lane.name, lane.state).toBeTruthy();
      expect(lane.sentence, lane.state).toMatch(/\.$/);
      expect(lane.tone, lane.state).toBeTruthy();
      expect(lane.glyph, lane.state).toBeTruthy();
    }
  });

  it('gives distinct lanes distinct names, so no two lanes read alike', () => {
    const names = LANE_PRESENTATIONS.map((lane) => lane.name);
    expect(new Set(names).size).toBe(names.length);
  });

  it('gives each canonical lane its own tone, so colour identifies the lane', () => {
    // Only the canonical lanes are required to be unique; the aliases
    // deliberately borrow their parent lane's tone.
    const tones = LANE_PRESENTATIONS.map((lane) => lane.tone);
    expect(new Set(tones).size).toBe(tones.length);
  });

  describe('the reported case: 5-human-review', () => {
    it('is named "Human review" — one word for the chip, the Result header, and the board', () => {
      expect(laneName(TaskState.HumanReview)).toBe('Human review');
    });

    it('never renders any of the four wordings that used to compete', () => {
      const retired = ['Review', 'Human review lane', 'Awaiting human review.', 'Human Review'];
      for (const wording of retired) {
        expect(laneName(TaskState.HumanReview)).not.toBe(wording);
        expect(laneSentence(TaskState.HumanReview)).not.toBe(wording);
      }
    });

    it('reads as one sentence and one tone token', () => {
      expect(laneSentence(TaskState.HumanReview)).toBe('Waiting for a human decision.');
      expect(laneTone(TaskState.HumanReview)).toBe('human-review');
    });

    it('does not share the Delivered tone — green stays reserved for Delivered', () => {
      expect(laneTone(TaskState.HumanReview)).not.toBe(laneTone(TaskState.Completed));
    });
  });

  describe('aliases', () => {
    it('gives the virtual Ready sub-lane the Preparation wording', () => {
      expect(laneName('2-ready-intake')).toBe('Preparation');
      expect(laneDocTopic('2-ready-intake')).toBe('lane-2-ready');
    });

    it('folds the legacy 4-review key onto Post Processing', () => {
      expect(laneName('4-review')).toBe(laneName(TaskState.AutoReview));
      expect(laneTone('4-review')).toBe(laneTone(TaskState.AutoReview));
    });

    it('folds the legacy 1b-needs-human-review key onto Human review', () => {
      expect(laneName('1b-needs-human-review')).toBe(laneName(TaskState.HumanReview));
      expect(laneTone('1b-needs-human-review')).toBe(laneTone(TaskState.HumanReview));
    });
  });

  describe('unknown lanes', () => {
    it('degrades a backend-first lane key to readable words', () => {
      expect(laneName('9-something-new')).toBe('Something new');
      expect(laneTone('9-something-new')).toBe('unknown');
      expect(laneDocTopic('9-something-new')).toBeNull();
    });

    it('handles a sub-lettered prefix', () => {
      expect(laneName('8b-parked')).toBe('Parked');
    });

    it('never throws on null, undefined, or empty input', () => {
      for (const input of [null, undefined, '']) {
        expect(() => lanePresentation(input)).not.toThrow();
        expect(laneTone(input)).toBe('unknown');
        expect(laneDocTopic(input)).toBeNull();
        expect(laneGlyph(input)).toBeTruthy();
      }
    });
  });

  it('exposes every renderable name for the lint gate', () => {
    expect(ALL_LANE_NAMES).toContain('Human review');
    expect(new Set(ALL_LANE_NAMES).size).toBe(ALL_LANE_NAMES.length);
  });

  it('points every lane with a help doc at a lane-guide topic', () => {
    for (const lane of LANE_PRESENTATIONS) {
      if (lane.docTopic === null) continue;
      expect(lane.docTopic, lane.state).toMatch(/^lane-/);
    }
  });
});

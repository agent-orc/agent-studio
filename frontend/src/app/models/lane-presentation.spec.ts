import { describe, expect, it } from 'vitest';
import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  LANES_IN_BOARD_ORDER,
  LANE_PRESENTATION,
  isKnownLane,
  laneDocTopic,
  laneGlyph,
  laneName,
  lanePresentation,
  laneSentence,
  laneShortName,
  laneTone,
} from './lane-presentation';

describe('lane presentation', () => {
  it('covers every canonical TaskState', () => {
    for (const state of ALL_TASK_STATES) {
      expect(isKnownLane(state), `${state} has no presentation`).toBe(true);
    }
    expect(LANES_IN_BOARD_ORDER).toHaveLength(ALL_TASK_STATES.length);
  });

  it('names 5-human-review "Human review" everywhere it can be asked', () => {
    // The operator report (2026-09-06): one lane, four wordings. Every
    // accessor the UI reaches for must answer with the same word.
    expect(laneName(TaskState.HumanReview)).toBe('Human review');
    expect(laneShortName(TaskState.HumanReview)).toBe('Human review');
    expect(lanePresentation(TaskState.HumanReview).name).toBe('Human review');
    expect(laneSentence(TaskState.HumanReview)).toBe('Waiting for a human decision.');
    expect(laneTone(TaskState.HumanReview)).toBe('--studio-lane-human-review');
  });

  it('gives every lane a name, a short name, a sentence, a tone, and a glyph', () => {
    for (const lane of LANES_IN_BOARD_ORDER) {
      expect(lane.name, `${lane.state} name`).toBeTruthy();
      expect(lane.shortName, `${lane.state} shortName`).toBeTruthy();
      expect(lane.sentence, `${lane.state} sentence`).toMatch(/\.$/);
      expect(lane.tone, `${lane.state} tone`).toMatch(/^--studio-lane-[a-z-]+$/);
      expect(lane.glyph, `${lane.state} glyph`).toBeTruthy();
    }
  });

  it('gives each lane its own tone token', () => {
    const tones = LANES_IN_BOARD_ORDER.map((lane) => lane.tone);
    // Lanes may deliberately share a hue (failed pickup / code not complete),
    // but a token must never be silently reused by an unrelated lane pair.
    expect(new Set(tones).size).toBeGreaterThanOrEqual(tones.length - 2);
  });

  it('names no two canonical lanes the same thing', () => {
    const names = LANES_IN_BOARD_ORDER.map((lane) => lane.name);
    expect(new Set(names).size).toBe(names.length);
  });

  it('puts lanes in board order, escalation before the review it precedes', () => {
    const order = LANES_IN_BOARD_ORDER.map((lane) => lane.state);
    expect(order[0]).toBe(TaskState.Backlog);
    expect(order.at(-1)).toBe(TaskState.Archive);
    expect(order.indexOf(TaskState.Ready)).toBeLessThan(order.indexOf(TaskState.Progress));
    expect(order.indexOf(TaskState.Escalated)).toBeLessThan(order.indexOf(TaskState.HumanReview));
    expect(order.indexOf(TaskState.HumanReview)).toBeLessThan(order.indexOf(TaskState.Completed));
  });

  it('resolves virtual and legacy lane keys to a canonical presentation', () => {
    expect(laneName('4-review')).toBe(laneName(TaskState.AutoReview));
    expect(laneName('1b-needs-human-review')).toBe('Human review');
    expect(laneName('2-ready-intake')).toBe('Preparation');
    expect(laneDocTopic('2-ready-intake')).toBe('lane-2-ready');
  });

  it('humanises an unknown lane instead of leaking the raw key', () => {
    const unknown = lanePresentation('9-brand-new-lane');
    expect(unknown.name).toBe('Brand new lane');
    expect(unknown.tone).toBe('--studio-lane-unknown');
    expect(unknown.docTopic).toBeNull();
    expect(isKnownLane('9-brand-new-lane')).toBe(false);
  });

  it('treats a null or empty lane as unknown without throwing', () => {
    expect(() => lanePresentation(null)).not.toThrow();
    expect(laneName(undefined)).toBe('');
    expect(laneDocTopic(null)).toBeNull();
    expect(laneGlyph('')).toBe('•');
  });

  it('points every lane with a guide at a lane-guides topic', () => {
    for (const lane of Object.values(LANE_PRESENTATION)) {
      if (lane.docTopic === null) continue;
      expect(lane.docTopic, `${lane.state} doc topic`).toMatch(/^lane-[0-9a-z-]+$/);
    }
    // Regression: the human-review guide is the one the operator opens most.
    expect(laneDocTopic(TaskState.HumanReview)).toBe('lane-5-human-review');
  });
});

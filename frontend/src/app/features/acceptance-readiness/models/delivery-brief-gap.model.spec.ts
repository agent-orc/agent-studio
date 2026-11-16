import { describe, expect, it } from 'vitest';
import { briefGapOf, briefGapSentence, unrecordedBriefSubject } from './delivery-brief-gap.model';

const current = 'bbbbbbbbbbbbbbbb';

describe('briefGapOf (F12)', () => {
  it('reads a missing delivery or verdict brief version as unrecorded, not changed', () => {
    expect(briefGapOf({ currentBriefVersion: current, deliveryBriefVersion: null, reviewBriefVersion: current }))
      .toBe('unrecorded');
    expect(briefGapOf({ currentBriefVersion: current, deliveryBriefVersion: current, reviewBriefVersion: undefined }))
      .toBe('unrecorded');
    expect(briefGapOf({ currentBriefVersion: current, deliveryBriefVersion: '  ', reviewBriefVersion: null }))
      .toBe('unrecorded');
  });

  it('reads a recorded version that differs from the current brief as changed', () => {
    expect(briefGapOf({ currentBriefVersion: current, deliveryBriefVersion: 'aaaa', reviewBriefVersion: current }))
      .toBe('changed');
    expect(briefGapOf({ currentBriefVersion: current, deliveryBriefVersion: null, reviewBriefVersion: 'aaaa' }))
      .toBe('changed');
  });

  it('has no gap when both versions are the current brief, or without a review', () => {
    expect(briefGapOf({ currentBriefVersion: current, deliveryBriefVersion: current, reviewBriefVersion: current }))
      .toBeNull();
    expect(briefGapOf(null)).toBeNull();
  });

  it('names the record that lacks a version', () => {
    expect(unrecordedBriefSubject({ currentBriefVersion: current, deliveryBriefVersion: null, reviewBriefVersion: current }))
      .toBe('this delivery');
    expect(unrecordedBriefSubject({ currentBriefVersion: current, deliveryBriefVersion: current, reviewBriefVersion: null }))
      .toBe('the content verdict of this delivery');
    expect(unrecordedBriefSubject({ currentBriefVersion: current, deliveryBriefVersion: null, reviewBriefVersion: null }))
      .toBe('this delivery or its content verdict');
    expect(briefGapSentence('unrecorded', { currentBriefVersion: current, deliveryBriefVersion: null, reviewBriefVersion: current }))
      .toBe('No brief version was recorded for this delivery.');
    expect(briefGapSentence('changed', { currentBriefVersion: current, deliveryBriefVersion: 'a', reviewBriefVersion: current }))
      .toBe('The brief changed after this delivery.');
  });
});

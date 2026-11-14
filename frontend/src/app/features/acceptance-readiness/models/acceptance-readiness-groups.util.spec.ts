import { describe, expect, it } from 'vitest';
import { groupByReadiness, readyJobs } from './acceptance-readiness-groups.util';
import type { AcceptanceReadinessItem } from './acceptance-readiness.model';
import { readinessItem, reviewCard } from '../testing/acceptance-readiness.fixtures';

const cards = ['a', 'b', 'c', 'd', 'e'].map(id => reviewCard(id));
const items = new Map<string, AcceptanceReadinessItem>([
  ['test::a', readinessItem('a', 'review-concerns')],
  ['test::b', readinessItem('b', 'ready')],
  ['test::c', readinessItem('c', 'waiting-deploy')],
  ['test::d', readinessItem('d', 'ready')],
]);

describe('groupByReadiness', () => {
  it('groups in display order, keeps unchecked cards, and drops empty groups', () => {
    const groups = groupByReadiness(cards, items, 'all');

    expect(groups.map(group => group.kind)).toEqual(['ready', 'review-concerns', 'waiting-deploy', 'unchecked']);
    expect(groups.map(group => group.jobs.map(job => job.id))).toEqual([['b', 'd'], ['a'], ['c'], ['e']]);
    expect(groups[0].label).toBe('Ready to accept');
    expect(groups[3].label).toBe('Not checked yet');
    expect(groups.reduce((sum, group) => sum + group.jobs.length, 0)).toBe(cards.length);
  });

  it('filters to ready cards or to the rest', () => {
    expect(groupByReadiness(cards, items, 'ready').flatMap(group => group.jobs.map(job => job.id))).toEqual(['b', 'd']);
    expect(groupByReadiness(cards, items, 'not-ready').flatMap(group => group.jobs.map(job => job.id))).toEqual(['a', 'c', 'e']);
  });

  it('offers only ready cards to accept', () => {
    expect(readyJobs(cards, items).map(job => job.id)).toEqual(['b', 'd']);
  });
});

import { describe, expect, it } from 'vitest';
import { PersonaEntry, PersonaItem, personaCards, poolTotals } from './persona-team.model';
const entry = (id: string, role: string): PersonaEntry => ({
  id, title: id, aspect: role, kind: 'persona', revision: '1', adoptableRevision: null,
  descriptor: { charter: '', cadence: { report: 'P1D' }, budget: { tokensPerWeek: 100 } },
  activation: { personaId: id, roleId: role, state: 'active', descriptorRevision: '1',
    allowance: { tokensPerWeek: 100 }, history: [] },
});
const item = (personaId: string, state: string, importance: number): PersonaItem => ({
  id: `${personaId}-${state}`, personaId, roleId: 'security', kind: 'finding', title: '', body: '',
  importance, state, createdAtUtc: '2026-11-12T00:00:00Z', impact: 5, urgency: 5, confidence: 1, floor: 0,
});
describe('persona team projection', () => {
  it('reconciles the pool from visible persona instances, including two in one role', () => {
    const entries = [entry('A', 'security'), entry('B', 'security')];
    const budget = { poolTokens: 400, usedTokens: 80, startUtc: '', endUtc: '', personas: {
      A: { allowanceTokens: 100, usedTokens: 30 }, B: { allowanceTokens: 150, usedTokens: 50 },
    } };
    const cards = personaCards(entries, budget, [], [], new Date('2026-11-13'));
    expect(cards).toHaveLength(2);
    expect(poolTotals(cards, budget.poolTokens)).toEqual({ used: 80, allocated: 250, unallocated: 150, pool: 400 });
  });
  it('ignores resolved Critical items when selecting the open acute signal', () => {
    const cards = personaCards([entry('A', 'security')], null,
      [item('A', 'resolved', 97), item('A', 'open', 54)], [], new Date('2026-11-13'));
    expect(cards[0].highest).toBe(54);
  });
});

import { describe, expect, it } from 'vitest';

import type { UsageCli } from './models/usage-cockpit.model';
import {
  cliAbbreviation,
  headerTierForWidth,
  narrowerTier,
  orderClis,
  planUsageHeader,
  type UsageHeaderMeasures,
} from './usage-header-layout';

const cli = (cliId: string) => ({ cliId } as UsageCli);

const measures = (overrides: Partial<UsageHeaderMeasures> = {}): UsageHeaderMeasures => ({
  primary: { full: 180, compact: 120, abbreviated: 90, bare: 60 },
  cost: { full: 140, compact: 110, abbreviated: 110, bare: 70 },
  secondaries: [170, 170, 170],
  details: 80,
  wordmark: 50,
  more: 44,
  gap: 12,
  gutter: 16,
  ...overrides,
});

describe('headerTierForWidth', () => {
  it('follows the 768 / 1200 / 1600 breakpoints', () => {
    expect(headerTierForWidth(320)).toBe('phone');
    expect(headerTierForWidth(767)).toBe('phone');
    expect(headerTierForWidth(768)).toBe('tablet');
    expect(headerTierForWidth(1199)).toBe('tablet');
    expect(headerTierForWidth(1200)).toBe('desktop');
    expect(headerTierForWidth(1600)).toBe('wide');
  });

  it('narrows one tier at a time and stops at phone', () => {
    expect(narrowerTier('wide')).toBe('desktop');
    expect(narrowerTier('tablet')).toBe('phone');
    expect(narrowerTier('phone')).toBe('phone');
  });
});

describe('orderClis', () => {
  const clis = [cli('codex'), cli('claude'), cli('gemini')];

  it('keeps stable provider order without a selection or default', () => {
    expect(orderClis(clis, null, null).map(c => c.cliId)).toEqual(['codex', 'claude', 'gemini']);
  });

  it('leads with the saved default, then the current selection over it', () => {
    expect(orderClis(clis, null, 'gemini').map(c => c.cliId)).toEqual(['gemini', 'codex', 'claude']);
    expect(orderClis(clis, 'claude', 'gemini').map(c => c.cliId)).toEqual(['claude', 'codex', 'gemini']);
  });

  it('ignores a selection or default that is not configured', () => {
    expect(orderClis(clis, 'copilot', 'claude').map(c => c.cliId)).toEqual(['claude', 'codex', 'gemini']);
  });
});

describe('cliAbbreviation', () => {
  it('uses established short names and keeps unknown providers recognisable', () => {
    expect(cliAbbreviation('Codex')).toBe('CX');
    expect(cliAbbreviation('enterprise-gateway-provider')).toBe('ENT');
  });
});

describe('planUsageHeader', () => {
  it('fits every whole secondary chip on desktop when there is room', () => {
    const plan = planUsageHeader(1728, 'wide', measures());
    expect(plan).toMatchObject({ layout: 'rows', fit: 'full', visibleSecondaries: 3, showDetails: true });
  });

  it('caps tablet at the primary plus the next CLI', () => {
    expect(planUsageHeader(1024, 'tablet', measures()).visibleSecondaries).toBe(1);
  });

  it('moves secondary chips out last first, never a partial chip', () => {
    // 32 gutters + 180 + 12 + 140 + 12 + 80 = 456; each secondary adds 182.
    expect(planUsageHeader(456 + 182 + 181, 'desktop', measures()).visibleSecondaries).toBe(1);
    expect(planUsageHeader(456 + 182 * 2, 'desktop', measures()).visibleSecondaries).toBe(2);
  });

  it('falls back to the phone composition inside the usage row when primary and cost do not fit', () => {
    const plan = planUsageHeader(800, 'tablet', measures({ primary: { full: 700, compact: 200, abbreviated: 150, bare: 100 } }));
    expect(plan).toMatchObject({ layout: 'rows', fit: 'compact', visibleSecondaries: 0 });
  });

  it('phone shows the wordmark until the values need its room', () => {
    expect(planUsageHeader(400, 'phone', measures())).toMatchObject({ layout: 'single', fit: 'compact', showWordmark: true, showDetails: false });
    // 32 + 50 + 12 + (120 + 12 + 110 + 12 + 44) = 392 does not fit 380; abbreviated does.
    expect(planUsageHeader(380, 'phone', measures())).toMatchObject({ fit: 'abbreviated', showWordmark: true });
    // Abbreviated without the wordmark: 32 + 90 + 12 + 110 + 12 + 44 = 300.
    expect(planUsageHeader(300, 'phone', measures())).toMatchObject({ fit: 'abbreviated', showWordmark: false });
    expect(planUsageHeader(260, 'phone', measures())).toMatchObject({ fit: 'bare', showWordmark: false });
  });
});

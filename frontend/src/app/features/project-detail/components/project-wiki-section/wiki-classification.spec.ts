import {
  classificationBadges,
  classificationMeta,
  classificationStatusTone,
  classificationTypeAbbreviation,
  classificationTypeLabel,
  formatAnalyzedDate,
} from './wiki-classification';
import { WikiClassification } from '../../../../models/project-docs.model';

const classification = (overrides: Partial<WikiClassification>): WikiClassification => ({
  status: null,
  supersededBy: null,
  type: null,
  analyzedAt: null,
  ...overrides,
});

describe('wiki-classification badges', () => {
  it('returns no badges for a missing classification', () => {
    expect(classificationBadges(null)).toEqual([]);
    expect(classificationBadges(undefined)).toEqual([]);
  });

  it('renders no status chip for aktuell or unclassified status', () => {
    const aktuell = classificationBadges(classification({ status: 'aktuell', type: 'konzept' }));
    expect(aktuell.map(b => b.key)).toEqual(['type']);
    const none = classificationBadges(classification({ type: 'proposal' }));
    expect(none.map(b => b.key)).toEqual(['type']);
  });

  it('renders a stale chip for veraltet with the analysis date in the tooltip only', () => {
    const badges = classificationBadges(
      classification({ status: 'veraltet', type: 'analyse', analyzedAt: '2026-07-18' }),
    );
    const status = badges.find(b => b.key === 'status')!;
    expect(status.label).toBe('veraltet');
    expect(status.tone).toBe('stale');
    expect(status.tooltip).toContain('Analyse 18.07.2026');
  });

  it('renders a superseded chip for ueberholt naming the successor in the tooltip', () => {
    const badges = classificationBadges(classification({
      status: 'ueberholt',
      supersededBy: 'workbenches/haertung-verteilte-ausfuehrung/historie.html',
      type: 'analyse',
      analyzedAt: '2026-07-18',
    }));
    const status = badges.find(b => b.key === 'status')!;
    expect(status.label).toBe('überholt');
    expect(status.tone).toBe('superseded');
    expect(status.tooltip).toContain('Überholt durch workbenches/haertung-verteilte-ausfuehrung/historie.html');
    expect(status.tooltip).toContain('Analyse 18.07.2026');
  });

  it('renders ueberholt without a successor gracefully', () => {
    const status = classificationBadges(classification({ status: 'ueberholt' }))
      .find(b => b.key === 'status')!;
    expect(status.tooltip).toBe('Überholt.');
  });

  it('renders the type as a muted 2-3-letter code with the full label in the tooltip', () => {
    const badges = classificationBadges(
      classification({ type: 'domain-map', analyzedAt: '2026-07-18' }),
    );
    expect(badges).toEqual([{
      key: 'type',
      label: 'DOM',
      tone: 'muted',
      tooltip: 'Typ: Domain-Map. · Analyse 18.07.2026',
    }]);
  });

  it('orders the status chip before the type code', () => {
    const badges = classificationBadges(
      classification({ status: 'veraltet', type: 'mockup' }),
    );
    expect(badges.map(b => b.key)).toEqual(['status', 'type']);
  });
});

describe('classificationTypeAbbreviation', () => {
  it('maps all agreed types to their compact codes', () => {
    const expected: Record<string, string> = {
      konzept: 'KON', adr: 'ADR', contract: 'CTR', 'domain-map': 'DOM',
      analyse: 'ANA', runbook: 'RUN', workbench: 'WB', mockup: 'MCK',
      proposal: 'PRP', generiert: 'GEN', index: 'IDX',
    };
    for (const [type, abbr] of Object.entries(expected)) {
      expect(classificationTypeAbbreviation(type)).toBe(abbr);
    }
  });

  it('shortens unknown types to three uppercase letters', () => {
    expect(classificationTypeAbbreviation('whitepaper')).toBe('WHI');
  });
});

describe('classificationTypeLabel', () => {
  it('spells curated types out in full', () => {
    expect(classificationTypeLabel('konzept')).toBe('Konzept');
    expect(classificationTypeLabel('domain-map')).toBe('Domain-Map');
    expect(classificationTypeLabel(' ADR ')).toBe('ADR');
  });

  it('falls back to the raw value for unknown types', () => {
    expect(classificationTypeLabel('whitepaper')).toBe('whitepaper');
  });
});

describe('classificationMeta (meta-rail block view model)', () => {
  it('returns null for a missing or empty classification (block hidden)', () => {
    expect(classificationMeta(null)).toBeNull();
    expect(classificationMeta(undefined)).toBeNull();
    expect(classificationMeta(classification({}))).toBeNull();
    expect(classificationMeta(classification({ status: '  ', type: ' ', analyzedAt: '' }))).toBeNull();
  });

  it('maps veraltet to a stale chip without a successor', () => {
    const meta = classificationMeta(classification({ status: 'veraltet' }))!;
    expect(meta.status).toEqual({ label: 'veraltet', tone: 'stale', supersededBy: null });
  });

  it('maps ueberholt to a superseded chip carrying the successor path', () => {
    const meta = classificationMeta(classification({
      status: 'ueberholt',
      supersededBy: 'concepts/new-overview.md',
    }))!;
    expect(meta.status).toEqual({
      label: 'überholt',
      tone: 'superseded',
      supersededBy: 'concepts/new-overview.md',
    });
  });

  it('shows aktuell as a muted chip (unlike the tree, which hides it)', () => {
    const meta = classificationMeta(classification({ status: 'aktuell' }))!;
    expect(meta.status).toEqual({ label: 'aktuell', tone: 'muted', supersededBy: null });
  });

  it('spells the type out and formats the analysis date', () => {
    const meta = classificationMeta(classification({
      type: 'domain-map',
      analyzedAt: '2026-07-18',
    }))!;
    expect(meta.status).toBeNull();
    expect(meta.typeLabel).toBe('Domain-Map');
    expect(meta.analyzedAt).toBe('18.07.2026');
  });

  it('ignores a blank successor on ueberholt', () => {
    const meta = classificationMeta(classification({ status: 'ueberholt', supersededBy: '  ' }))!;
    expect(meta.status!.supersededBy).toBeNull();
  });
});

describe('formatAnalyzedDate', () => {
  it('formats an ISO date as dd.mm.yyyy', () => {
    expect(formatAnalyzedDate('2026-07-18')).toBe('18.07.2026');
  });

  it('falls back to the raw value for non-ISO input', () => {
    expect(formatAnalyzedDate('Juli 2026')).toBe('Juli 2026');
  });
});

describe('classificationStatusTone', () => {
  it('maps veraltet to danger, überholt to warning and everything else to neutral', () => {
    expect(classificationStatusTone('stale')).toBe('danger');
    expect(classificationStatusTone('superseded')).toBe('warning');
    expect(classificationStatusTone('muted')).toBe('neutral');
    expect(classificationStatusTone(null)).toBe('neutral');
    expect(classificationStatusTone(undefined)).toBe('neutral');
  });
});

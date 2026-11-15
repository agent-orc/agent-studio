import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiDashboardComponent, driftGradeTone } from './wiki-dashboard.component';
import type { WikiPulse } from '../../../../../models/project-docs.model';

function isoHoursAgo(hours: number): string {
  return new Date(Date.now() - hours * 3_600_000).toISOString();
}

const PULSE: WikiPulse = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  generatedAtUtc: new Date().toISOString(),
  feed: {
    available: true,
    reason: null,
    items: [
      {
        relPath: 'concepts/overview.md',
        title: 'Concept overview',
        author: 'Alice',
        authorDateUtc: isoHoursAgo(3),
        sha: 'a1',
        shortSha: 'a1',
        subject: 'refine overview',
        areaSlug: null,
        areaTitle: null,
        taskKey: 'AGT-2014',
      },
    ],
  },
  inbox: { available: true, reason: null, count: 0, items: [] },
  drift: {
    available: true,
    reason: null,
    overallGrade: 'Aging',
    areas: [
      { slug: '10-current-development-state', title: 'Current Development State', grade: 'Aging', pageCount: 2, gradedPageCount: 2, worstCommitCount: 12, freshCount: 1, agingCount: 1, staleCount: 0 },
    ],
    counts: { fresh: 1, aging: 1, stale: 0, graded: 2 },
  },
  critical: { available: true, reason: 'No pages graded yet.', count: 0, overallGrade: 'none', items: [] },
};

async function mount(pulse: WikiPulse | null = PULSE, docCount = 7) {
  await TestBed.configureTestingModule({
    imports: [WikiDashboardComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(WikiDashboardComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('docCount', docCount);
  fixture.componentRef.setInput('pulse', pulse);
  fixture.detectChanges();
  // Card components fetch their own payloads (curated home links, style guides).
  http.expectOne('/api/projects/Demo/wiki/home').flush({ sections: [] });
  http.expectOne('/api/projects/Demo/style-guides').flush({
    projectKey: 'PROJ-1', projectDisplayName: 'Demo', technologies: [], guides: [],
    warnings: [], snapshotId: '0123456789abcdef', capturedAtUtc: new Date().toISOString(),
    refreshAfterUtc: new Date().toISOString(),
  });
  fixture.detectChanges();
  return { fixture, http };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiDashboardComponent', () => {
  it('renders the head stat tiles from the Pulse payload', async () => {
    const { fixture, http } = await mount();
    const root = el(fixture);

    expect(root.querySelector('[data-testid="project-wiki-stat-pages"]')?.textContent).toContain('7');
    expect(root.querySelector('[data-testid="project-wiki-stat-pages"]')?.textContent).toContain('Seiten');
    // Newest feed entry is 3h old -> a "vor X h" caption.
    expect(root.querySelector('[data-testid="project-wiki-stat-edited"]')?.textContent).toContain('vor 3 h');

    // Drift counts render as three plain numbers with labels; the overall
    // verdict is a compact status indicator, never a coloured number.
    const drift = root.querySelector('[data-testid="project-wiki-stat-drift"]')!;
    expect(drift.textContent).toContain('Fresh');
    expect(drift.textContent).toContain('Aging');
    expect(drift.textContent).toContain('Stale');
    const overall = root.querySelector('[data-testid="project-wiki-pulse-overall"]')!;
    expect(overall.textContent).toContain('Drift Aging');
    expect(overall.getAttribute('data-tone')).toBe('warning');
    http.verify();
  });

  it('falls back to placeholders while the Pulse payload is missing', async () => {
    const { fixture, http } = await mount(null);
    const root = el(fixture);
    expect(root.querySelector('[data-testid="project-wiki-stat-edited"]')?.textContent).toContain('–');
    expect(root.querySelector('[data-testid="project-wiki-pulse-overall"]')).toBeNull();
    http.verify();
  });

  it('emits the quick-action intents and mounts the card grid', async () => {
    const { fixture, http } = await mount();
    const root = el(fixture);
    let opened = 0;
    let drifted = 0;
    fixture.componentInstance.openFirst.subscribe(() => opened++);
    fixture.componentInstance.openDrift.subscribe(() => drifted++);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-open-first"]')!.click();
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-drift-open-empty"]')!.click();
    expect(opened).toBe(1);
    expect(drifted).toBe(1);

    const grid = root.querySelector('[data-testid="project-wiki-dash-grid"]')!;
    // No stars -> no starred card; Pulse cards and the wide cards are mounted.
    expect(grid.querySelector('app-wiki-starred-panel')).toBeNull();
    expect(grid.querySelector('[data-testid="project-wiki-pulse-drift"]')).toBeTruthy();
    expect(grid.querySelector('[data-testid="project-wiki-pulse-feed"]')).toBeTruthy();
    expect(grid.querySelector('[data-testid="project-wiki-style-guides"]')).toBeTruthy();
    expect(grid.querySelector('[data-testid="project-wiki-pulse-grading"]')).toBeTruthy();
    http.verify();
  });
});

describe('driftGradeTone', () => {
  it('maps every drift grade onto a status tone, unknown grades stay neutral', () => {
    expect(driftGradeTone('Fresh')).toBe('success');
    expect(driftGradeTone('Aging')).toBe('warning');
    expect(driftGradeTone('Stale')).toBe('danger');
    expect(driftGradeTone('Empty')).toBe('neutral');
    expect(driftGradeTone('Something new')).toBe('neutral');
    expect(driftGradeTone(null)).toBe('neutral');
  });
});

import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiPulseComponent, WikiPulseOpenRequest } from './wiki-pulse.component';
import type { WikiPulse } from '../../../../../models/project-docs.model';

function isoDaysAgo(days: number, hour = 9): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  d.setHours(hour, 0, 0, 0);
  return d.toISOString();
}

function makePulse(overrides: Partial<WikiPulse> = {}): WikiPulse {
  return {
    projectName: 'Demo',
    baseDir: '/repo/docs',
    exists: true,
    generatedAtUtc: new Date().toISOString(),
    feed: {
      available: true,
      reason: null,
      items: [
        {
          relPath: 'concepts/active.md',
          title: 'Active stream',
          author: 'Alice',
          authorDateUtc: isoDaysAgo(0),
          sha: 'a1',
          shortSha: 'a1',
          subject: 'AGT-2014 update',
          areaSlug: 'concepts',
          areaTitle: 'concepts',
          taskKey: 'AGT-2014',
        },
        {
          relPath: 'notes.md',
          title: 'Notes',
          author: 'Bob',
          authorDateUtc: isoDaysAgo(1),
          sha: 'b1',
          shortSha: 'b1',
          subject: 'jot notes',
          areaSlug: null,
          areaTitle: null,
          taskKey: null,
        },
      ],
    },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true,
      reason: null,
      overallGrade: 'Stale',
      areas: [
        { slug: 'concepts', title: 'concepts', grade: 'Stale', pageCount: 1, gradedPageCount: 1, worstCommitCount: 60, freshCount: 0, agingCount: 0, staleCount: 1 },
        { slug: 'operations', title: 'operations', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
      ],
      counts: { fresh: 0, aging: 0, stale: 1, graded: 1 },
    },
    critical: { available: true, reason: 'No pages have been graded yet.', count: 0, overallGrade: 'none', items: [] },
    warnings: {
      available: true,
      reason: null,
      count: 1,
      items: [{ kind: 'human-action', title: 'Runner crash', detail: 'Development signal is active.', humanAction: 'Inspect the failed run.', relPath: 'signals/runner.md', status: 'active' }],
    },
    activity: {
      available: true,
      reason: null,
      runs: [{ taskKey: 'AGT-2015', lane: '3-progress', startedAtUtc: isoDaysAgo(0), docsFilesChanged: 2 }],
    },
    ...overrides,
  };
}

async function mount(pulse: WikiPulse | null) {
  await TestBed.configureTestingModule({
    imports: [WikiPulseComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(WikiPulseComponent);
  fixture.componentRef.setInput('pulse', pulse);
  fixture.detectChanges();
  return fixture;
}

const html = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiPulseComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('shows a compact feed (8 rows) and reveals the rest behind "Alle anzeigen"', async () => {
    const items = Array.from({ length: 11 }, (_, i) => ({
      relPath: `notes-${i}.md`,
      title: `Note ${i}`,
      author: 'Alice',
      authorDateUtc: isoDaysAgo(0),
      sha: `s${i}`,
      shortSha: `s${i}`,
      subject: 'edit',
      areaSlug: null,
      areaTitle: null,
      taskKey: null,
    }));
    const fixture = await mount(makePulse({ feed: { available: true, reason: null, items } }));
    const root = html(fixture);

    const rows = () => root.querySelectorAll('[data-testid^="project-wiki-pulse-feed-open-"]');
    expect(rows().length).toBe(8);

    // Pure UI state: expanding shows every row, collapsing trims back to 8.
    const toggle = () => root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-pulse-feed-toggle"]')!;
    expect(toggle().textContent).toContain('Alle anzeigen');
    toggle().click();
    fixture.detectChanges();
    expect(rows().length).toBe(11);
    expect(toggle().textContent).toContain('Weniger anzeigen');
    toggle().click();
    fixture.detectChanges();
    expect(rows().length).toBe(8);
  });

  it('hides the feed toggle when the feed already fits the compact card', async () => {
    const fixture = await mount(makePulse());
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-feed-toggle"]')).toBeNull();
  });

  it('renders the top-folder badge and the task key on a feed row', async () => {
    const fixture = await mount(makePulse());
    const root = html(fixture);
    const badge = root.querySelector('[data-testid="project-wiki-pulse-area-badge-concepts/active.md"]');
    expect(badge?.textContent).toContain('concepts');
    const task = root.querySelector('[data-testid="project-wiki-pulse-task-concepts/active.md"]');
    expect(task?.textContent).toContain('AGT-2014');
  });

  it('maps drift grades to tones on the grade bar', async () => {
    const fixture = await mount(makePulse());
    const root = html(fixture);
    const stale = root.querySelector('[data-testid="project-wiki-pulse-area-concepts"]');
    expect(stale?.getAttribute('data-tone')).toBe('bad');
    const empty = root.querySelector('[data-testid="project-wiki-pulse-area-operations"]');
    expect(empty?.getAttribute('data-tone')).toBe('muted');
  });

  it('emits openPage when a feed row is clicked, deriving the node type', async () => {
    const fixture = await mount(makePulse());
    let emitted: WikiPulseOpenRequest | null = null;
    fixture.componentInstance.openPage.subscribe(r => (emitted = r));
    html(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-pulse-feed-open-notes.md"]')!
      .click();
    expect(emitted).toEqual({ relPath: 'notes.md', type: 'md' });
  });

  it('drops the Aufmerksamkeit card entirely when warnings and inbox are clear', async () => {
    const fixture = await mount(makePulse({
      inbox: { available: true, reason: null, count: 0, items: [] },
      warnings: { available: true, reason: 'All clear.', count: 0, items: [] },
    }));
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-attention"]')).toBeNull();
  });

  it('keeps the Aufmerksamkeit card when warnings exist even though the inbox is clear', async () => {
    const fixture = await mount(makePulse({ inbox: { available: true, reason: null, count: 0, items: [] } }));
    const root = html(fixture);
    expect(root.querySelector('[data-testid="project-wiki-pulse-attention"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-warnings"]')?.textContent).toContain('Runner crash');
    // A clear inbox contributes no sub-list to the merged card.
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox"]')).toBeNull();
  });

  it('degrades each section to a reason when its source is unavailable', async () => {
    const fixture = await mount(makePulse({
      feed: { available: false, reason: 'No git repository resolved for this project.', items: [] },
      inbox: { available: false, reason: 'No docs/ folder for this project yet.', count: 0, items: [] },
      drift: { available: false, reason: 'No git repository resolved for this project.', overallGrade: 'Empty', areas: [], counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
    }));
    const root = html(fixture);
    expect(root.querySelector('[data-testid="project-wiki-pulse-feed-unavailable"]')?.textContent).toContain('No git repository');
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox-unavailable"]')?.textContent).toContain('No docs/ folder');
    expect(root.querySelector('[data-testid="project-wiki-pulse-drift-unavailable"]')).toBeTruthy();
  });

  it('shows the drift empty-state hint when no pages are graded', async () => {
    const fixture = await mount(makePulse({
      drift: {
        available: true,
        reason: 'No knowledge pages in any top-level docs folder yet.',
        overallGrade: 'Empty',
        areas: [
          { slug: 'concepts', title: 'concepts', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
        ],
        counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
      },
    }));
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-drift-empty"]')?.textContent)
      .toContain('No knowledge pages in any top-level docs folder');
  });

  it('renders actionable warnings and docs-touching live runs', async () => {
    const fixture = await mount(makePulse());
    const root = html(fixture);
    expect(root.querySelector('[data-testid="project-wiki-pulse-warnings"]')?.textContent).toContain('Inspect the failed run.');
    const status = root.querySelector('[data-testid="project-wiki-pulse-warning-status"]');
    expect(status?.getAttribute('data-tone')).toBe('warning');
    expect(status?.textContent?.trim()).toBe('active');
    expect(root.querySelector('[data-testid="project-wiki-pulse-live-AGT-2015"]')?.textContent).toContain('2 docs files changed');
  });
});

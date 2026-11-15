import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiGradePanelComponent, gradingStateTone } from './wiki-grade-panel.component';
import type { WikiPulseCritical, WikiGradingRunStatus } from '../../../../../models/project-docs.model';

const CRITICAL: WikiPulseCritical = {
  available: true,
  reason: null,
  count: 2,
  overallGrade: 'D',
  items: [
    { relPath: 'poor.md', title: 'Poor page', grade: 'D', assessment: 'Needs a rewrite.', gradedAt: null, model: 'claude-sonnet-5', reportPath: 'poor.md.report.html', areaTitle: null },
    { relPath: 'weak.md', title: 'Weak page', grade: 'C', assessment: 'Needs a refresh.', gradedAt: null, model: 'claude-sonnet-5', reportPath: 'weak.md.report.html', areaTitle: null },
  ],
};

const RUNNING: WikiGradingRunStatus = {
  projectName: 'Demo', runId: 'wg-1', state: 'running', cliType: 'claude', model: 'claude-sonnet-5',
  thinkingLevel: null, force: false, total: 10, processed: 4, graded: 3, skipped: 1, failed: 0,
  critical: 1, currentRelPath: 'concepts/x.md', startedAtUtc: new Date().toISOString(),
  completedAtUtc: null, error: null, recent: [],
};

async function mount(inputs: Record<string, unknown> = {}) {
  await TestBed.configureTestingModule({
    imports: [WikiGradePanelComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(WikiGradePanelComponent);
  for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
  fixture.detectChanges();
  return fixture;
}

const html = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiGradePanelComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('shows the "Grade all pages" trigger when no run is in flight', async () => {
    const fixture = await mount();
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-grade-start"]')).toBeTruthy();
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-grade-abort"]')).toBeNull();
  });

  it('emits startGrading when the trigger button is clicked', async () => {
    const fixture = await mount();
    let started = false;
    fixture.componentInstance.startGrading.subscribe(() => (started = true));
    html(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-pulse-grade-start"]')!.click();
    expect(started).toBe(true);
  });

  it('renders progress + abort while a run is in flight', async () => {
    const fixture = await mount({ gradingStatus: RUNNING });
    const root = html(fixture);
    expect(root.querySelector('[data-testid="project-wiki-pulse-grade-abort"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-grade-start"]')).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-pulse-grade-state"]')?.textContent).toContain('4/10');
    expect(root.querySelector('[data-testid="project-wiki-pulse-grade-state"]')?.getAttribute('data-tone')).toBe('running');
    expect(root.querySelector<HTMLElement>('.wgrade__fill')?.style.width).toBe('40%');
  });

  it('lists critical pages worst-first and emits openReport on click', async () => {
    const fixture = await mount({ critical: CRITICAL });
    const rows = Array.from(html(fixture).querySelectorAll('[data-testid^="project-wiki-pulse-critical-open-"]'));
    expect(rows.length).toBe(2);
    expect(rows[0].getAttribute('data-testid')).toContain('poor.md');

    let emitted: string | null = null;
    fixture.componentInstance.openReport.subscribe(r => (emitted = r));
    (rows[0] as HTMLButtonElement).click();
    expect(emitted).toBe('poor.md');
  });

  it('shows the healthy critical empty-state when nothing is critical', async () => {
    const fixture = await mount({
      critical: { available: true, reason: 'No critical pages: every graded page is B or better.', count: 0, overallGrade: 'none', items: [] },
    });
    const empty = html(fixture).querySelector('[data-testid="project-wiki-pulse-critical-empty"]');
    expect(empty?.getAttribute('data-tone')).toBe('success');
    expect(empty?.textContent).toContain('every graded page is B or better');
  });

  it('emits gradeModelChange when a model is picked from the dropdown', async () => {
    const fixture = await mount({
      gradeModel: 'claude-sonnet-5',
      gradeModels: [
        { id: 'claude-sonnet-5', label: 'Claude Sonnet 5', vendor: 'anthropic', isDefault: true, available: true, thinkingLevels: [], defaultThinkingLevel: null },
        { id: 'claude-opus-4-8', label: 'Claude Opus 4.8', vendor: 'anthropic', isDefault: false, available: true, thinkingLevels: [], defaultThinkingLevel: null },
      ],
    });
    let picked: string | null = null;
    fixture.componentInstance.gradeModelChange.subscribe(v => (picked = v));
    const select = html(fixture).querySelector<HTMLSelectElement>('[data-testid="project-wiki-pulse-grade-model"]')!;
    select.value = 'claude-opus-4-8';
    select.dispatchEvent(new Event('change'));
    expect(picked).toBe('claude-opus-4-8');
  });
});

describe('gradingStateTone', () => {
  it('maps each grading run state onto a status tone, unknown states stay neutral', () => {
    expect(gradingStateTone('running')).toBe('running');
    expect(gradingStateTone('completed')).toBe('success');
    expect(gradingStateTone('aborted')).toBe('neutral');
    expect(gradingStateTone('failed')).toBe('danger');
    expect(gradingStateTone('queued')).toBe('neutral');
    expect(gradingStateTone(null)).toBe('neutral');
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkbenchDocument, WorkbenchPromotion } from '../../../../models/project-docs.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import { DossierPromotionNoticeComponent } from './dossier-promotion-notice.component';

const DOCUMENT = {
  workbench: {
    id: 'integrierte-plattform', key: 'AGT-W81', title: 'Integrierte Plattform',
    summary: 'Plattform', status: 'decision-pending', phase: 'decision-ready',
    updatedAtUtc: '2026-10-16T20:30:00Z', entryPath: 'docs/operations/integrierte-plattform/index.html',
    valid: true, error: null, sourceTaskKeys: [],
  },
  html: '', branch: 'develop', revision: 'a'.repeat(40), workingTreeModified: false, fingerprint: 'b'.repeat(64),
} as WorkbenchDocument;

const NO_CARD: WorkbenchPromotion = {
  available: false, reason: 'no-concept-card',
  message: 'This Dossier has no source concept card. Implementation cards come from a concept card\'s reviewed Dossier.',
  sourceTaskKey: null, sourceJobId: null, sourceTitle: null, sourceLane: null, proposalCount: 0,
};

describe('DossierPromotionNoticeComponent', () => {
  let fixture: ComponentFixture<DossierPromotionNoticeComponent>;
  const createJob = vi.fn(() => of({ id: 'concept-review-w81' }));
  const getDetailByProject = vi.fn(() => of({ info: { key: 'AGT-3900', displayKey: 'AGT-3900' } }));
  const getWatchPaths = vi.fn(() => of([{ name: 'Agent Studio', path: '/tasks/agent-studio' }]));
  const refresh = vi.fn();
  const openReferenceOrNotify = vi.fn(() => true);
  const byTestId = <T extends HTMLElement>(id: string): T | null =>
    (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${id}"]`);

  beforeEach(async () => {
    createJob.mockClear();
    openReferenceOrNotify.mockClear();
    await TestBed.configureTestingModule({
      imports: [DossierPromotionNoticeComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { createJob, getDetailByProject, getWatchPaths, refresh } },
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DossierPromotionNoticeComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('promotion', NO_CARD);
    fixture.detectChanges();
  });

  it('says why a Dossier without a concept card cannot promote and creates one in Preparation', () => {
    expect(byTestId('dossier-promotion-reason')?.textContent).toContain('no source concept card');
    expect(byTestId('concept-promotion-open')).toBeNull();

    byTestId<HTMLButtonElement>('dossier-promotion-create-concept')!.click();
    fixture.detectChanges();

    expect(createJob).toHaveBeenCalledWith(expect.objectContaining({
      mode: 'concept', targetState: '1-preparation', watchPath: '/tasks/agent-studio',
      dossier: { path: 'docs/operations/integrierte-plattform/index.html' },
    }));
    const prompt = (createJob.mock.calls[0] as unknown as [{ promptMarkdown: string }])[0].promptMarkdown;
    expect(prompt).toContain('sourceTaskKeys');
    expect(prompt).toContain('record-only');
    expect(byTestId('dossier-promotion-created')?.textContent).toContain('Created AGT-3900 in Preparation');
    byTestId<HTMLButtonElement>('dossier-promotion-open-card')!.click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('AGT-3900');
  });

  it('opens the concept card when its Dossier was never concept-reviewed', () => {
    fixture.componentRef.setInput('promotion', {
      ...NO_CARD, reason: 'no-review', sourceTaskKey: 'AGT-3600',
      message: 'Concept card AGT-3600 has not delivered a reviewed Dossier yet.',
    });
    fixture.detectChanges();

    expect(byTestId('dossier-promotion-reason')?.textContent).toContain('has not delivered a reviewed Dossier');
    expect(byTestId('dossier-promotion-create-concept')).toBeNull();
    const open = byTestId<HTMLButtonElement>('dossier-promotion-open-card')!;
    expect(open.textContent).toContain('Open AGT-3600 to start the concept review');
    open.click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('AGT-3600');
  });

  it('offers no action in the read-only demo', () => {
    fixture.componentRef.setInput('readOnly', true);
    fixture.detectChanges();
    expect(byTestId('dossier-promotion-create-concept')).toBeNull();
    expect(byTestId('dossier-promotion-reason')).not.toBeNull();
  });
});

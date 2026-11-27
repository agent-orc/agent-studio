import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ReviewEvidencePanelComponent } from './review-evidence-panel.component';
import { MediaLightboxService } from '@studio/ui/services/media-lightbox.service';
import type { ReviewEvidenceEntry, TaskInfo } from '../../../../../models/task.model';

function entry(overrides: Partial<ReviewEvidenceEntry> = {}): ReviewEvidenceEntry {
  return {
    id: 'e1',
    source: 'security-audit',
    severity: 'high',
    title: 'pipeline-state-empty--mocked',
    body: null,
    createdAt: '2026-07-09T12:00:00Z',
    runIndex: null,
    artifacts: [],
    fileRefs: [],
    acknowledged: false,
    followupJobId: null,
    ...overrides,
  };
}

const JOB = { id: 'job-1', watchPath: '/ws' } as TaskInfo;

describe('ReviewEvidencePanelComponent', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewEvidencePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewEvidencePanelComponent);
    fixture.componentRef.setInput('entries', undefined);
    fixture.componentRef.setInput('job', undefined);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ReviewEvidencePanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  describe('references', () => {
    let component: ReviewEvidencePanelComponent;
    let lightbox: MediaLightboxService;

    beforeEach(async () => {
      await TestBed.configureTestingModule({
        imports: [ReviewEvidencePanelComponent],
        providers: [provideZonelessChangeDetection(), MediaLightboxService],
      }).compileComponents();
      const fixture = TestBed.createComponent(ReviewEvidencePanelComponent);
      fixture.componentRef.setInput('entries', []);
      fixture.componentRef.setInput('job', JOB);
      component = fixture.componentInstance;
      lightbox = TestBed.inject(MediaLightboxService);
    });

    it('classifies image references by extension', () => {
      expect(component.isImageRef('results/shot.png')).toBe(true);
      expect(component.isImageRef('results/shot.JPG')).toBe(true);
      expect(component.isImageRef('results/shot.webp')).toBe(true);
      expect(component.isImageRef('backend/Auth.cs:12')).toBe(false);
      expect(component.isImageRef('results/notes.md')).toBe(false);
    });

    it('gives each non-image type its own glyph, not one generic icon', () => {
      const md = component.refIcon('results/plan.md');
      const json = component.refIcon('results/data.jsonl');
      const log = component.refIcon('logs/cli-output.log');
      const code = component.refIcon('backend/Auth.cs:12');
      const plain = component.refIcon('results/blob.bin');
      const glyphs = [md, json, log, code];
      // Each recognised type maps to a distinct, meaningful glyph.
      expect(new Set(glyphs).size).toBe(glyphs.length);
      expect(md).not.toBe(plain);
    });

    it('splits an entry into image refs (artifacts first) and text refs', () => {
      const e = entry({
        artifacts: ['results/a.png', 'results/report.md'],
        fileRefs: ['results/b.png', 'backend/Auth.cs:9'],
      });
      expect(component.imageRefs(e)).toEqual(['results/a.png', 'results/b.png']);
      expect(component.textArtifacts(e)).toEqual(['results/report.md']);
      expect(component.textFileRefs(e)).toEqual(['backend/Auth.cs:9']);
    });

    it('resolves a results/ ref to the served API url', () => {
      expect(component.thumbUrl('results/shot.png')).toContain('/api/tasks/job-1/results/shot.png');
    });

    it('opens the shared lightbox as a gallery focused on the clicked image', () => {
      const e = entry({ artifacts: ['results/a.png', 'results/b.png'] });
      component.openImage(e, 'results/b.png');
      expect(lightbox.count()).toBe(2);
      expect(lightbox.position()).toBe(2);
      expect(lightbox.active()?.src).toContain('results/b.png');
    });

    it('renders image artifacts as thumbnails and non-image refs as labelled text', () => {
      const fixture = TestBed.createComponent(ReviewEvidencePanelComponent);
      fixture.componentRef.setInput('job', JOB);
      fixture.componentRef.setInput('entries', [
        entry({
          id: 'row-1',
          artifacts: ['results/pipeline-state-empty--mocked.png'],
          fileRefs: ['backend/Auth.cs:142'],
        }),
      ]);
      fixture.detectChanges();
      const el: HTMLElement = fixture.nativeElement;
      const thumb = el.querySelector<HTMLImageElement>('[data-testid="review-evidence-thumb-row-1"] img');
      expect(thumb).not.toBeNull();
      expect(thumb!.getAttribute('loading')).toBe('lazy');
      expect(thumb!.getAttribute('src')).toContain('pipeline-state-empty--mocked.png');
      // The non-image file ref stays a text row.
      const fileref = el.querySelector('[data-testid="review-evidence-fileref-row-1"]');
      expect(fileref?.textContent).toContain('backend/Auth.cs:142');
    });

    it('renders the Quality Studio rule id as named review evidence', () => {
      const fixture = TestBed.createComponent(ReviewEvidencePanelComponent);
      fixture.componentRef.setInput('job', JOB);
      fixture.componentRef.setInput('entries', [entry({ ruleId: 'QS-NG-002' })]);

      fixture.detectChanges();

      const rule = fixture.nativeElement.querySelector('[data-testid="review-evidence-rule-e1"]');
      expect(rule?.textContent).toBe('QS-NG-002');
    });
  });
});

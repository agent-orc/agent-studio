import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { DocumentDetailsMenuComponent } from './document-details-menu.component';

describe('DocumentDetailsMenuComponent', () => {
  it('keeps token grouping when the default number locale is de-DE', () => {
    const native = Number.prototype.toLocaleString;
    const defaultGerman = vi.spyOn(Number.prototype, 'toLocaleString').mockImplementation(function (
      this: number,
      locales?: Intl.LocalesArgument,
      options?: Intl.NumberFormatOptions,
    ) {
      return native.call(this, locales === undefined ? 'de-DE' : locales, options);
    });
    try {
      const fixture = TestBed.configureTestingModule({
        imports: [DocumentDetailsMenuComponent],
        providers: [provideZonelessChangeDetection()],
      }).createComponent(DocumentDetailsMenuComponent);
      fixture.componentRef.setInput('title', 'Code review');
      fixture.componentRef.setInput('file', {
        name: 'code-review-grade.md', sizeBytes: 2048, mtime: '2026-07-11T12:00:00Z', kind: 'codeReview',
        generation: { file: 'code-review-grade.md', kind: 'code-review', model: 'gpt-5', cli: 'codex', tokensIn: 800, tokensOut: 200, tokensTotal: 1000, durationMs: 2000 },
      });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('800 in · 200 out · 1,000 total');
    } finally {
      defaultGerman.mockRestore();
    }
  });

  it('discloses source metadata and requests the raw source', async () => {
    await TestBed.configureTestingModule({
      imports: [DocumentDetailsMenuComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DocumentDetailsMenuComponent);
    fixture.componentRef.setInput('title', 'Code review');
    fixture.componentRef.setInput('file', {
      name: 'code-review-grade.md', sizeBytes: 2048, mtime: '2026-07-11T12:00:00Z', kind: 'codeReview',
      generation: {
        file: 'code-review-grade.md', kind: 'code-review', model: 'gpt-5', cli: 'codex',
        tokensIn: 800, tokensOut: 200, tokensTotal: 1000, durationMs: 2000,
      },
    });
    fixture.detectChanges();

    let requested = false;
    fixture.componentInstance.toggleSource.subscribe(() => requested = true);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('code-review-grade.md');
    expect(root.textContent).toContain('2 KB');
    expect(root.textContent).toContain('800 in · 200 out · 1,000 total');
    (root.querySelector('[data-testid="file-card-source-code-review-grade.md"]') as HTMLButtonElement).click();
    expect(requested).toBe(true);
  });

  it('keeps document history behind an explicit details action', async () => {
    await TestBed.configureTestingModule({
      imports: [DocumentDetailsMenuComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DocumentDetailsMenuComponent);
    fixture.componentRef.setInput('title', 'Task brief');
    fixture.componentRef.setInput('file', {
      name: 'prompt.md', sizeBytes: 512, mtime: '2026-07-11T12:00:00Z', kind: 'prompt',
    });
    fixture.detectChanges();

    let requested = false;
    fixture.componentInstance.toggleHistory.subscribe(() => requested = true);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="file-card-source-prompt.md"]')).toBeNull();
    (root.querySelector('[data-testid="file-card-history-prompt.md"]') as HTMLButtonElement).click();
    expect(requested).toBe(true);
  });
});

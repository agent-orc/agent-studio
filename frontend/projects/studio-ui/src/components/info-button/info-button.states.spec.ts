import { TestBed } from '@angular/core/testing';
import { describe, it, expect, vi } from 'vitest';
import { InfoButtonComponent } from './info-button.component';
import { STUDIO_HELP_LOADER, type HelpDocument } from './help-loader';

describe('InfoButtonComponent host document loading', () => {
  it('shows loading, renders the supplied document and reuses it when reopened', async () => {
    let resolveDocument!: (document: HelpDocument) => void;
    const load = vi.fn(() => new Promise<HelpDocument>(resolve => { resolveDocument = resolve; }));
    TestBed.configureTestingModule({ imports: [InfoButtonComponent], providers: [{ provide: STUDIO_HELP_LOADER, useValue: load }] });
    await TestBed.compileComponents();
    const fixture = TestBed.createComponent(InfoButtonComponent);
    fixture.componentRef.setInput('topic', 'example');
    fixture.detectChanges();
    fixture.componentInstance.toggle(new Event('click'));
    expect(fixture.componentInstance.loading()).toBe(true);
    resolveDocument({ topic: 'example', title: 'Host document', body: 'Help supplied by the host.' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.componentInstance.title()).toBe('Host document');
    expect(fixture.componentInstance.body()).toBe('Help supplied by the host.');
    fixture.componentInstance.close();
    fixture.componentInstance.toggle(new Event('click'));
    expect(load).toHaveBeenCalledOnce();
  });

  it('keeps the existing missing-document wording when the host refuses the topic', async () => {
    TestBed.configureTestingModule({ imports: [InfoButtonComponent], providers: [{ provide: STUDIO_HELP_LOADER, useValue: () => Promise.reject({ status: 404 }) }] });
    await TestBed.compileComponents();
    const fixture = TestBed.createComponent(InfoButtonComponent);
    fixture.componentRef.setInput('topic', 'missing');
    fixture.detectChanges();
    fixture.componentInstance.toggle(new Event('click'));
    await fixture.whenStable();
    expect(fixture.componentInstance.errorMessage()).toBe('No concept doc found for "missing".');
    expect(fixture.componentInstance.loading()).toBe(false);
  });
});

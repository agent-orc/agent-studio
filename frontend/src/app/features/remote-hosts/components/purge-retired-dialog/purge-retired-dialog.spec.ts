import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PurgeRetiredDialogComponent } from './purge-retired-dialog';

describe('PurgeRetiredDialogComponent', () => {
  function mount() {
    TestBed.configureTestingModule({
      imports: [PurgeRetiredDialogComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(PurgeRetiredDialogComponent);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  it('previews e2e- prefixed retired hosts before allowing a delete', () => {
    const { fixture, http, el } = mount();

    expect((el.querySelector('[data-testid="purge-retired-prefix"]') as HTMLInputElement).value).toBe('e2e-');
    expect(el.querySelector('[data-testid="purge-retired-confirm"]')).toBeFalsy();

    (el.querySelector('[data-testid="purge-retired-preview"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const req = http.expectOne('/api/clients/retired/purge');
    expect(req.request.body).toEqual({ prefix: 'e2e-', dryRun: true });
    req.flush({
      dryRun: true,
      results: [
        { id: 'e2e-owner-alpha', displayName: 'e2e-owner-Alpha', outcome: 'would-delete' },
        { id: 'e2e-owner-busy', displayName: 'e2e-owner-Busy', outcome: 'skipped-active-lease' },
      ],
    });
    fixture.detectChanges();

    const list = el.querySelector('[data-testid="purge-retired-list"]');
    expect(list?.textContent).toContain('e2e-owner-Alpha');
    expect(list?.textContent).toContain('e2e-owner-Busy');

    const confirm = el.querySelector('[data-testid="purge-retired-confirm"]') as HTMLButtonElement;
    expect(confirm).toBeTruthy();
    expect(confirm.textContent).toContain('Delete 1 permanently');
    expect(confirm.disabled).toBe(false);

    http.verify();
  });

  it('disables the confirm action when nothing would be deleted', () => {
    const { fixture, http, el } = mount();
    (el.querySelector('[data-testid="purge-retired-preview"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    http.expectOne('/api/clients/retired/purge').flush({ dryRun: true, results: [] });
    fixture.detectChanges();

    const confirm = el.querySelector('[data-testid="purge-retired-confirm"]') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    expect(el.querySelector('[data-testid="purge-retired-list"]')?.textContent)
      .toContain('No retired hosts match this prefix.');
    http.verify();
  });

  it('applies the purge and reports the deleted count on close', () => {
    const { fixture, http, el } = mount();
    (el.querySelector('[data-testid="purge-retired-preview"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    http.expectOne('/api/clients/retired/purge').flush({
      dryRun: true,
      results: [{ id: 'e2e-owner-alpha', displayName: 'e2e-owner-Alpha', outcome: 'would-delete' }],
    });
    fixture.detectChanges();

    let purgedCount: number | null = null;
    fixture.componentInstance.purged.subscribe(count => { purgedCount = count; });

    (el.querySelector('[data-testid="purge-retired-confirm"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const req = http.expectOne('/api/clients/retired/purge');
    expect(req.request.body).toEqual({ prefix: 'e2e-', dryRun: false });
    req.flush({
      dryRun: false,
      results: [{ id: 'e2e-owner-alpha', displayName: 'e2e-owner-Alpha', outcome: 'deleted' }],
    });
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="purge-retired-done"]')?.textContent).toContain('1 deleted.');
    (el.querySelector('[data-testid="purge-retired-close"]') as HTMLButtonElement).click();

    expect(purgedCount).toBe(1);
    http.verify();
  });
});

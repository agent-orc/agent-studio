import { beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { AcceptanceRoundDialogComponent } from './acceptance-round-dialog.component';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { readinessItem, reviewCard } from '../../testing/acceptance-readiness.fixtures';

describe('AcceptanceRoundDialogComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AcceptanceRoundDialogComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('prefills the prompt and result ref and sends only the edited round on confirm', () => {
    const store = TestBed.inject(AcceptanceReadinessStore);
    const send = vi.spyOn(store, 'sendRound').mockResolvedValue(true);
    const fixture = TestBed.createComponent(AcceptanceRoundDialogComponent);
    fixture.componentRef.setInput('draft', { job: reviewCard('b'), item: readinessItem('b', 'review-concerns') });
    fixture.detectChanges();

    const prompt = document.querySelector<HTMLTextAreaElement>('[data-testid="acceptance-round-prompt"]')!;
    const ref = document.querySelector<HTMLInputElement>('[data-testid="acceptance-round-base-ref"]')!;
    expect(prompt.value).toBe('Close the acceptance gap on this card.');
    expect(ref.value).toBe('refs/heads/runner/r1/AGT-1');
    expect(send).not.toHaveBeenCalled();

    prompt.value = 'Show the reason on the card.';
    prompt.dispatchEvent(new Event('input'));
    document.querySelector<HTMLButtonElement>('[data-testid="acceptance-round-send"]')!.click();
    expect(send).toHaveBeenCalledWith('Show the reason on the card.', 'refs/heads/runner/r1/AGT-1');
    fixture.destroy();
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { preserveOwnProperty } from '../../../testing/preserve-own-property';
import { SecretRevealComponent } from './secret-reveal.component';

describe('SecretRevealComponent', () => {
  preserveOwnProperty(navigator, 'clipboard');
  /** Assembled at runtime so no credential-shaped literal lands in source. */
  const secret = ['sat', 'tok_1', 'secret-value'].join('.');

  function mount() {
    TestBed.configureTestingModule({ imports: [SecretRevealComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(SecretRevealComponent);
    fixture.componentRef.setInput('title', 'New token for deploy-bot');
    fixture.componentRef.setInput('value', secret);
    fixture.componentRef.setInput('note', 'It is not shown again.');
    fixture.componentRef.setInput('copyLabel', 'Copy token');
    fixture.componentRef.setInput('testId', 'issued');
    fixture.detectChanges();
    return fixture;
  }

  it('marks the secret as shown once and copies it on request', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const fixture = mount();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="issued"]')!.textContent).toContain('Shown once');
    expect(root.querySelector('[data-testid="issued-value"]')!.textContent).toBe(secret);
    await fixture.componentInstance.copy();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith(secret);
    expect(root.querySelector('[data-testid="issued-copy"]')!.textContent).toBe('Copied');
    fixture.destroy();
  });

  it('says so when the clipboard refuses, and emits dismissed on Done', async () => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => Promise.reject(new Error('denied')) }, configurable: true });
    const fixture = mount();
    const root = fixture.nativeElement as HTMLElement;
    const dismissed: number[] = [];
    fixture.componentInstance.dismissed.subscribe(() => dismissed.push(1));

    await fixture.componentInstance.copy();
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')!.textContent).toContain('Copy failed');

    (root.querySelector('[data-testid="issued-done"]') as HTMLButtonElement).click();
    expect(dismissed).toEqual([1]);
    fixture.destroy();
  });
});

import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SettingsJumpLinksComponent } from './settings-jump-links';

describe('SettingsJumpLinksComponent', () => {
  afterEach(() => document.getElementById('jump-target')?.remove());

  it('scrolls to and focuses the target section without changing the route', async () => {
    const target = document.createElement('section');
    target.id = 'jump-target';
    Object.defineProperty(target, 'scrollIntoView', { configurable: true, writable: true, value: vi.fn() });
    document.body.appendChild(target);
    const hash = location.hash;
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(SettingsJumpLinksComponent);
    fixture.componentRef.setInput('links', [{ target: 'jump-target', label: 'Batch verification', testId: 'jump-batch' }]);
    await fixture.whenStable();
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('[data-testid="jump-batch"]');
    expect(button.textContent).toContain('Batch verification');
    button.click();
    expect(target.scrollIntoView).toHaveBeenCalled();
    expect(target.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(target);
    expect(location.hash).toBe(hash);
  });
});

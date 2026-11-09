/* eslint-disable @angular-eslint/component-max-inline-declarations -- test-only host for the directive */
import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { SettingFocusDirective } from './setting-focus.directive';

@Component({
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [SettingFocusDirective],
  template: `<section appSettingFocus [focusSettingKey]="key()">
    @if (loaded()) { <div data-setting-key="integration-branch"><input data-testid="branch" /></div> }
    <div data-setting-key="repository-visibility"><button type="button">Private</button></div>
  </section>`,
})
class HostComponent {
  readonly key = signal<string | undefined>(undefined);
  readonly loaded = signal(false);
}

describe('SettingFocusDirective', () => {
  it('focuses the named setting once its card renders, and moves with the key', async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.componentInstance.key.set('integration-branch');
    fixture.detectChanges();
    await Promise.resolve();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[aria-current]')).toBeNull();

    fixture.componentInstance.loaded.set(true);
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve));
    const branch = root.querySelector('[data-setting-key="integration-branch"]')!;
    expect(branch.getAttribute('aria-current')).toBe('location');
    expect(document.activeElement).toBe(root.querySelector('[data-testid="branch"]'));

    fixture.componentInstance.key.set('repository-visibility');
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve));
    expect(branch.hasAttribute('aria-current')).toBe(false);
    const visibility = root.querySelector<HTMLElement>('[data-setting-key="repository-visibility"]')!;
    expect(visibility.getAttribute('aria-current')).toBe('location');
    expect(document.activeElement).toBe(visibility);
    fixture.destroy();
  });
});

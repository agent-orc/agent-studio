import { TestBed } from '@angular/core/testing';
import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { beforeEach, describe, expect, it } from 'vitest';
import { RowComponent } from './row.component';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RowComponent],
  templateUrl: './row-density-test-host.html',
})
class RowHost {
  readonly variant = signal<'compact' | 'default' | 'cozy' | undefined>(undefined);
}

describe('RowComponent density inheritance', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [RowComponent, RowHost],
    providers: [provideZonelessChangeDetection()],
  }));

  it('leaves the variant unset so a shared row inherits root or surface aliases', () => {
    const fixture = TestBed.createComponent(RowComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.hasAttribute('data-variant')).toBe(false);
    expect(fixture.nativeElement.hasAttribute('tabindex')).toBe(false);
  });

  it.each(['compact', 'default', 'cozy'] as const)('reflects an explicit %s override and permits returning to inheritance', variant => {
    const fixture = TestBed.createComponent(RowHost);
    fixture.componentInstance.variant.set(variant);
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    const row = root.querySelector('app-row')!;
    expect(row.getAttribute('data-variant')).toBe(variant);
    expect(root.querySelector('[data-testid="app-row-value"]')?.textContent?.trim()).toBe('Unchanged data');
    expect(root.querySelector('button')?.textContent?.trim()).toBe('Inspect');
    fixture.componentInstance.variant.set(undefined);
    fixture.detectChanges();
    expect(row.hasAttribute('data-variant')).toBe(false);
    expect(root.querySelectorAll('button')).toHaveLength(1);
  });
});

/* eslint-disable @angular-eslint/prefer-on-push-component-change-detection, @angular-eslint/component-max-inline-declarations */
import { describe, expect, it } from 'vitest';
import { Component } from '@angular/core';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ListRowComponent } from './list-row.component';

@Component({
  standalone: true,
  imports: [ListRowComponent],
  template: `
    <app-list-row
      [label]="label"
      [interactive]="interactive"
      [active]="active"
      [capitalize]="capitalize"
      [indent]="indent"
      [testid]="testid"
      (activated)="clicks = clicks + 1">
      <span lead class="lead-marker">L</span>
      <span trail class="trail-marker">T</span>
    </app-list-row>
  `,
})
class HostComponent {
  label = 'claude';
  interactive = false;
  active = false;
  capitalize = false;
  indent: string | null = null;
  testid: string | null = null;
  clicks = 0;
}

/**
 * Inputs are configured BEFORE the single initial change detection so the
 * OnPush row never sees a post-render mutation — the zoneless harness throws
 * NG0100 otherwise. Each assertion gets its own fixture.
 */
function mount(configure?: (host: HostComponent) => void) {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(HostComponent);
  configure?.(fixture.componentInstance);
  fixture.detectChanges();
  return fixture;
}

describe('ListRowComponent', () => {
  it('renders a static div by default and projects lead/label/trail', () => {
    const fixture = mount();
    const el = fixture.nativeElement as HTMLElement;
    const row = el.querySelector('.list-row')!;
    expect(row.tagName).toBe('DIV');
    expect(row.querySelector('.lead-marker')?.textContent).toBe('L');
    expect(row.querySelector('.trail-marker')?.textContent).toBe('T');
    expect(row.querySelector('.list-row__label')?.textContent?.trim()).toBe('claude');
  });

  it('renders a button and emits activated on click when interactive', () => {
    const fixture = mount((host) => (host.interactive = true));
    const el = fixture.nativeElement as HTMLElement;
    const btn = el.querySelector('button.list-row') as HTMLButtonElement;
    expect(btn).toBeTruthy();
    btn.click();
    expect(fixture.componentInstance.clicks).toBe(1);
  });

  it('applies the active wash only when [active] is set', () => {
    const inactive = mount((host) => (host.interactive = true));
    expect(
      (inactive.nativeElement as HTMLElement).querySelector('.list-row')!.classList.contains('list-row--active'),
    ).toBe(false);

    const active = mount((host) => {
      host.interactive = true;
      host.active = true;
    });
    expect(
      (active.nativeElement as HTMLElement).querySelector('.list-row')!.classList.contains('list-row--active'),
    ).toBe(true);
  });

  it('capitalizes the label via the --caps modifier when [capitalize]', () => {
    const plain = mount();
    expect(
      (plain.nativeElement as HTMLElement).querySelector('.list-row__label')!.classList.contains('list-row__label--caps'),
    ).toBe(false);

    const caps = mount((host) => (host.capitalize = true));
    expect(
      (caps.nativeElement as HTMLElement).querySelector('.list-row__label')!.classList.contains('list-row__label--caps'),
    ).toBe(true);
  });

  it('overrides left padding when [indent] is set and forwards [testid]', () => {
    const fixture = mount((host) => {
      host.indent = '30px';
      host.testid = 'row-1';
    });
    const row = (fixture.nativeElement as HTMLElement).querySelector('.list-row') as HTMLElement;
    expect(row.style.paddingLeft).toBe('30px');
    expect(row.getAttribute('data-testid')).toBe('row-1');
  });
});

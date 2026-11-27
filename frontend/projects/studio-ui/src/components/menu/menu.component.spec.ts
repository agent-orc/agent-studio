import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { beforeEach, describe, expect, it } from 'vitest';
import { MenuComponent } from './menu.component';
import { MenuItem, MenuItemClickEvent } from './menu.types';

@Component({
  standalone: true,
  imports: [MenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './menu.component.spec.host.html',
})
class MenuHostComponent {
  readonly items = signal<readonly MenuItem[]>([]);
  readonly open = signal(false);
  readonly lastClick = signal<MenuItemClickEvent | null>(null);
}

function flush(): Promise<void> {
  return new Promise(resolve => queueMicrotask(resolve));
}

describe('MenuComponent', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<MenuHostComponent>>;
  let host: MenuHostComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MenuHostComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(MenuHostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  function panel(): HTMLElement | null {
    return document.querySelector<HTMLElement>('[data-testid="probe-panel"]');
  }
  function rowById(id: string): HTMLButtonElement | null {
    return document.querySelector<HTMLButtonElement>(`[data-testid="probe-item-${id}"]`);
  }
  function rowsAll(): HTMLButtonElement[] {
    return Array.from(document.querySelectorAll<HTMLButtonElement>('[data-testid^="probe-item-"]'));
  }

  it('renders nothing while closed', () => {
    host.items.set([{ kind: 'row', id: 'a', label: 'A' }]);
    fixture.detectChanges();
    expect(panel()).toBeNull();
  });

  it('renders rows, headers, and separators with correct roles', async () => {
    host.items.set([
      { kind: 'header', label: 'System' },
      { kind: 'row', id: 'a', label: 'A' },
      { kind: 'separator' },
      { kind: 'row', id: 'b', label: 'B' },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const p = panel();
    expect(p).not.toBeNull();
    expect(p!.getAttribute('role')).toBe('menu');

    const rows = rowsAll();
    expect(rows.length).toBe(2);
    rows.forEach(r => expect(r.getAttribute('role')).toBe('menuitem'));

    const sep = document.querySelector('.app-menu__separator');
    expect(sep?.getAttribute('role')).toBe('separator');
    const header = document.querySelector('.app-menu__header');
    expect(header?.textContent?.trim()).toBe('System');
  });

  it('clicking a row emits itemClick + closeRequest', async () => {
    host.items.set([
      { kind: 'row', id: 'go', label: 'Go' },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    rowById('go')!.click();
    fixture.detectChanges();

    expect(host.lastClick()?.id).toBe('go');
    expect(host.open()).toBe(false);
  });

  it('a disabled row is not clickable and does not emit itemClick', async () => {
    host.items.set([
      { kind: 'row', id: 'off', label: 'Off', disabled: true },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const row = rowById('off')!;
    expect(row.getAttribute('aria-disabled')).toBe('true');
    expect(row.disabled).toBe(true);
    row.click();
    fixture.detectChanges();
    expect(host.lastClick()).toBeNull();
  });

  it('a danger row carries the danger modifier class for the red accent', async () => {
    host.items.set([
      { kind: 'row', id: 'rm', label: 'Delete', danger: true },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const row = rowById('rm')!;
    expect(row.classList.contains('app-menu__row--danger')).toBe(true);
  });

  it('an active row exposes aria-current=true and the active class', async () => {
    host.items.set([
      { kind: 'row', id: 'cur', label: 'Current', active: true },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const row = rowById('cur')!;
    expect(row.getAttribute('aria-current')).toBe('true');
    expect(row.classList.contains('app-menu__row--active')).toBe(true);
  });

  it('leadingGlyph renders the coloured initial chip; trailingBadge renders the count', async () => {
    host.items.set([
      {
        kind: 'row',
        id: 'p',
        label: 'My Project',
        leadingGlyph: { background: '#abcdef', initial: 'M' },
        trailingBadge: '7',
      },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const row = rowById('p')!;
    const glyph = row.querySelector<HTMLElement>('.app-menu__glyph');
    expect(glyph?.textContent?.trim()).toBe('M');
    expect(glyph?.style.background).toContain('rgb(171, 205, 239)');
    const badge = row.querySelector('.app-menu__badge');
    expect(badge?.textContent?.trim()).toBe('7');
  });

  it('keyboard ArrowDown moves focus past separators and headers (skips non-row)', async () => {
    host.items.set([
      { kind: 'header', label: 'Section' },
      { kind: 'row', id: 'a', label: 'A' },
      { kind: 'separator' },
      { kind: 'row', id: 'b', label: 'B' },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    // First focusable row starts focused on open.
    expect(document.activeElement?.getAttribute('data-testid')).toBe('probe-item-a');

    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();
    expect(document.activeElement?.getAttribute('data-testid')).toBe('probe-item-b');
  });

  it('Arrow nav skips disabled rows', async () => {
    host.items.set([
      { kind: 'row', id: 'a', label: 'A' },
      { kind: 'row', id: 'b', label: 'B', disabled: true },
      { kind: 'row', id: 'c', label: 'C' },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();
    // 'a' → 'c' (skipping disabled 'b')
    expect(document.activeElement?.getAttribute('data-testid')).toBe('probe-item-c');
  });

  it('Enter on a focused row activates it and emits closeRequest', async () => {
    host.items.set([
      { kind: 'row', id: 'a', label: 'A' },
    ]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();
    expect(host.lastClick()?.id).toBe('a');
    expect(host.open()).toBe(false);
  });

  it('Escape on the panel closes the menu', async () => {
    host.items.set([{ kind: 'row', id: 'a', label: 'A' }]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(host.open()).toBe(false);
  });

  it('Escape returns focus to the trigger instead of the body (AGT-3831)', async () => {
    host.items.set([{ kind: 'row', id: 'a', label: 'A' }]);
    const trigger = document.querySelector<HTMLButtonElement>('[data-testid="host-trigger"]')!;
    trigger.focus();
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    expect(document.activeElement).toBe(rowById('a'));

    rowById('a')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(host.open()).toBe(false);
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('selecting a row returns focus to the trigger before itemClick fires (AGT-3831)', async () => {
    host.items.set([{ kind: 'row', id: 'a', label: 'A' }]);
    const trigger = document.querySelector<HTMLButtonElement>('[data-testid="host-trigger"]')!;
    let focusAtClick: Element | null = null;
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    const menu = fixture.debugElement.query(By.directive(MenuComponent)).componentInstance as MenuComponent;
    const sub = menu.itemClick.subscribe(() => { focusAtClick = document.activeElement; });

    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();
    expect(focusAtClick).toBe(trigger);
    expect(document.activeElement).toBe(trigger);
    sub.unsubscribe();
  });

  it('the owner closing the menu while a row has focus keeps focus off the body (AGT-3831)', async () => {
    host.items.set([{ kind: 'row', id: 'a', label: 'A' }]);
    const trigger = document.querySelector<HTMLButtonElement>('[data-testid="host-trigger"]')!;
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    expect(document.activeElement).toBe(rowById('a'));

    host.open.set(false);
    fixture.detectChanges();
    expect(document.activeElement).toBe(trigger);
  });

  it('the panel testId is composed from testIdPrefix', async () => {
    host.items.set([{ kind: 'row', id: 'x', label: 'X' }]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    expect(panel()!.getAttribute('data-testid')).toBe('probe-panel');
  });

  it('clicking the backdrop emits closeRequest', async () => {
    host.items.set([{ kind: 'row', id: 'x', label: 'X' }]);
    host.open.set(true);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const backdrop = document.querySelector<HTMLElement>('[data-testid="app-menu-backdrop"]');
    expect(backdrop).not.toBeNull();
    backdrop!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(host.open()).toBe(false);
  });
});

import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { MenuItem } from '@studio/ui/components/menu/index';
import { OverflowMenuComponent } from './overflow-menu.component';

const settle = () => new Promise<void>(resolve => queueMicrotask(resolve));

describe('OverflowMenuComponent', () => {
  const items: MenuItem[] = [
    { kind: 'row', id: 'open', label: 'Open details' },
    { kind: 'separator' },
    { kind: 'row', id: 'disable', label: 'Disable…', danger: true },
  ];

  it('opens the shared menu from a named trigger and emits the chosen row id', async () => {
    TestBed.configureTestingModule({
      imports: [OverflowMenuComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(OverflowMenuComponent);
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('label', 'Actions for deploy-bot');
    fixture.componentRef.setInput('testId', 'row-menu');
    document.body.append(fixture.nativeElement);
    fixture.detectChanges();
    const chosen: string[] = [];
    fixture.componentInstance.itemSelect.subscribe(id => chosen.push(id));
    const trigger = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="row-menu"]') as HTMLButtonElement;

    expect(trigger.getAttribute('aria-label')).toBe('Actions for deploy-bot');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    trigger.click();
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    expect(trigger.getAttribute('aria-expanded')).toBe('true');

    (document.querySelector('[data-testid="row-menu-item-disable"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(chosen).toEqual(['disable']);
    expect(fixture.componentInstance.open()).toBe(false);
    fixture.destroy();
  });
});

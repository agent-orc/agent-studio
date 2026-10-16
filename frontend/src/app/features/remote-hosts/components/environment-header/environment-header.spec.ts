import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { EnvironmentHeaderComponent } from './environment-header';

describe('EnvironmentHeaderComponent', () => {
  it('shows the selected view and emits a view change', async () => {
    await TestBed.configureTestingModule({ imports: [EnvironmentHeaderComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(EnvironmentHeaderComponent);
    fixture.componentRef.setInput('view', 'list');
    fixture.componentRef.setInput('retiredCount', 0);
    fixture.componentRef.setInput('loading', false);
    fixture.detectChanges();
    const selected: string[] = [];
    fixture.componentInstance.viewChange.subscribe(view => selected.push(view));
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h2')?.textContent).toBe('Environment');
    (element.querySelector('[data-testid="environment-map-toggle"]') as HTMLButtonElement).click();
    expect(selected).toEqual(['map']);
    fixture.destroy();
  });
});

import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ModelLevelIndicatorComponent } from './model-level-indicator.component';
import { CliCatalogStore } from '../../features/cli';

describe('ModelLevelIndicatorComponent', () => {
  it('shows an explained xhigh mapping only when discovery lacks ultra', async () => {
    await TestBed.configureTestingModule({
      imports: [ModelLevelIndicatorComponent],
      providers: [provideZonelessChangeDetection(), { provide: CliCatalogStore, useValue: {
        modelsFor: vi.fn().mockReturnValue([
          { id: 'gpt-6-luna', thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'] },
          { id: 'gpt-6-sol', thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'] },
        ]),
      } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(ModelLevelIndicatorComponent);
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('model', 'gpt-6-luna');
    fixture.componentRef.setInput('thinkingLevel', 'ultra');
    fixture.componentRef.setInput('tooltip', 'Pinned model');
    await fixture.whenStable();

    const level = fixture.nativeElement.querySelector('[data-testid="model-level-thinking"]') as HTMLElement;
    expect(level.textContent?.trim()).toBe('xh*');
    expect(level.dataset['levelMapping']).toBe('ultra-to-xhigh');
    expect(fixture.componentInstance.effectiveTooltip()).toContain('Pinned ultra is unavailable');

    fixture.componentRef.setInput('model', 'gpt-6-sol');
    await fixture.whenStable();
    expect(level.textContent?.trim()).toBe('u');
    expect(level.dataset['levelMapping']).toBeUndefined();
  });
});

import { beforeEach, afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ThemeService } from '../../services/theme.service';
import { AppearanceSettingsComponent } from './appearance-settings.component';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';

describe('AppearanceSettingsComponent', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [AppearanceSettingsComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([]),
      ],
    }).compileComponents();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    delete document.documentElement.dataset['studioDensity'];
  });

  it('renders a compact default and updates density through the shared preference', () => {
    const fixture = TestBed.createComponent(AppearanceSettingsComponent);
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    const group = root.querySelector('[role="group"][aria-label="Surface density"]');
    expect(group).not.toBeNull();
    const buttons = Array.from(group!.querySelectorAll('button'));
    expect(buttons.map(button => button.textContent?.trim())).toEqual(['Compact', 'Default', 'Cozy']);
    expect(buttons[0].getAttribute('aria-pressed')).toBe('true');
    buttons[2].click();
    fixture.detectChanges();
    expect(buttons[2].getAttribute('aria-pressed')).toBe('true');
    expect(localStorage.getItem('atp.studio.surfaceDensity.v1')).toBe('cozy');
    expect(document.documentElement.dataset['studioDensity']).toBe('cozy');
    TestBed.inject(UiPreferencesService).setSurfaceDensity('default');
    fixture.detectChanges();
    expect(buttons[1].getAttribute('aria-pressed')).toBe('true');
    expect(buttons.filter(button => button.getAttribute('aria-pressed') === 'true')).toHaveLength(1);
  });
  it('lists previews, restores the selected option and selects through the theme service', async () => {
    await TestBed.configureTestingModule({
      imports: [AppearanceSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    TestBed.inject(ThemeService).setTokenSet('preview-3');
    const fixture = TestBed.createComponent(AppearanceSettingsComponent);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const select = host.querySelector<HTMLSelectElement>('[data-testid="settings-token-set"]')!;
    expect(select.value).toBe('preview-3');
    expect([...select.options].map(option => option.text)).toEqual([
      'Studio', 'Preview 1 - calm and dense', 'Preview 2 - editorial', 'Preview 3 - workbench',
    ]);
    for (const id of ['preview-1', 'preview-2', 'preview-3']) {
      expect(host.querySelector(`[data-testid="settings-token-description-${id}"]`)?.textContent).toContain('(Preview)');
    }
    select.value = 'preview-2';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(TestBed.inject(ThemeService).tokenSetId()).toBe('preview-2');
    fixture.destroy();
    TestBed.resetTestingModule();
    localStorage.removeItem('atp.studio.tokenSet');
  });

});

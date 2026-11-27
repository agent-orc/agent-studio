import { beforeEach, afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
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
});

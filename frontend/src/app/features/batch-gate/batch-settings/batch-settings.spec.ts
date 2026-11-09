import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { BatchSettingsComponent } from './batch-settings';
import { batchSettings } from '../testing/batch-fixtures';

describe('BatchSettingsComponent', () => {
  it('shows field validation from a 400 response', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate').flush(batchSettings);
    await fixture.whenStable();
    fixture.componentInstance.save();
    http.expectOne('/api/projects/Demo/batch-gate').flush({ error: 'Maximum exceeds limit', field: 'maximumSize' }, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[name="maximumSize"]').getAttribute('aria-invalid')).toBe('true');
    expect(fixture.nativeElement.textContent).toContain('Maximum exceeds limit');
    http.verify();
  });

  it('says when the configured advisor cannot run on this server', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate').flush({
      ...batchSettings,
      advisorEffective: { effectiveMode: 'off', carrier: 'none', reason: 'The networked profile does not start the advisor CLI.' },
    });
    await fixture.whenStable();
    const note = fixture.nativeElement.querySelector('[data-testid="batch-settings-advisor-off"]');
    expect(note?.textContent).toContain('Advisor is off on this server.');
    expect(note?.textContent).toContain('The networked profile does not start the advisor CLI.');
    http.verify();
  });

  it('shows no advisor note when the host carries the advisor', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate').flush({
      ...batchSettings,
      advisorEffective: { effectiveMode: 'enforce', carrier: 'engine', reason: null },
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="batch-settings-advisor-off"]')).toBeNull();
    http.verify();
  });

  it('defaults the scope to code when the gate is enabled on documentation scope', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate').flush({ ...batchSettings, settings: { ...batchSettings.settings, enabled: false, scope: 'documentation' } });
    await fixture.whenStable();
    fixture.componentInstance.setEnabled(true);
    await fixture.whenStable();
    expect(fixture.componentInstance.settings()?.scope).toBe('code');
    expect(fixture.nativeElement.querySelector('[data-testid="batch-settings-scope-defaulted"]')?.textContent).toContain('Scope set to Code.');
    fixture.componentInstance.setScope('documentation');
    await fixture.whenStable();
    expect(fixture.componentInstance.settings()?.scope).toBe('documentation');
    expect(fixture.nativeElement.querySelector('[data-testid="batch-settings-scope-defaulted"]')).toBeNull();
    http.verify();
  });

  it('keeps an explicit documentation scope when the gate is already enabled', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate').flush({ ...batchSettings, settings: { ...batchSettings.settings, enabled: true, scope: 'documentation' } });
    await fixture.whenStable();
    fixture.componentInstance.setEnabled(true);
    expect(fixture.componentInstance.settings()?.scope).toBe('documentation');
    expect(fixture.componentInstance.scopeDefaulted()).toBe(false);
    http.verify();
  });

  it('shows calibration and stacking and saves their edits', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate').flush({ ...batchSettings, settings: { ...batchSettings.settings, calibration: 'legacy', stacking: '1' } });
    await fixture.whenStable();
    await fixture.whenStable();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector<HTMLSelectElement>('[data-testid="batch-settings-calibration"]')?.value).toBe('legacy');
    expect(element.querySelector<HTMLSelectElement>('[data-testid="batch-settings-stacking"]')?.value).toBe('1');
    fixture.componentInstance.update('calibration', 'empirical');
    fixture.componentInstance.update('stacking', 'off');
    fixture.componentInstance.save();
    const put = http.expectOne('/api/projects/Demo/batch-gate');
    expect(put.request.body.calibration).toBe('empirical');
    expect(put.request.body.stacking).toBe('off');
    put.flush({ ...batchSettings });
    http.verify();
  });

  it('applies the backend calibration and stacking defaults when the server omits them', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchSettingsComponent);
    fixture.componentRef.setInput('project', 'Demo');
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    const older: Partial<typeof batchSettings.settings> = { ...batchSettings.settings };
    delete older.calibration;
    delete older.stacking;
    http.expectOne('/api/projects/Demo/batch-gate').flush({ ...batchSettings, settings: older });
    await fixture.whenStable();
    expect(fixture.componentInstance.settings()?.calibration).toBe('empirical');
    expect(fixture.componentInstance.settings()?.stacking).toBe('off');
    http.verify();
  });
});

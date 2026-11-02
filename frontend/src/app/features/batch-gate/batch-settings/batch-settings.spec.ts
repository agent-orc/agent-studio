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
});

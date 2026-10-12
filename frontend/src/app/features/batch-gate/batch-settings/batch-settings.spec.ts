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
});

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { BatchPanelComponent } from './batch-panel';
import { redHalvingState } from '../testing/batch-fixtures';

describe('BatchPanelComponent', () => {
  it('shows halving, advisor veto and fallback membership from the state contract', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture: ComponentFixture<BatchPanelComponent> = TestBed.createComponent(BatchPanelComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('closed', () => {});
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Advisor veto');
    expect(text).toContain('veto');
    expect(text).toContain('halving');
    expect(text).toContain('test-failure');
    expect(text).toContain('agent-runner-01');
    http.verify();
  });
});

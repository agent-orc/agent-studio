import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { AuthSessionState } from '../../../../services/auth.service';
import { UnpricedModelsComponent } from './unpriced-models.component';

describe('UnpricedModelsComponent', () => {
  it('lists active unpriced models with calls and tokens for an owner', async () => {
    await TestBed.configureTestingModule({
      imports: [UnpricedModelsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ profile: 'local' }) } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(UnpricedModelsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController).expectOne('/api/token-pricing/unpriced-models').flush({
      models: [{ model: 'gpt-6.2-nova', calls: 3, totalTokens: 2420000 }], checkedAt: '2026-11-16T00:00:00Z',
      weekStartUtc: '2026-11-15T23:00:00Z', weekEndUtc: '2026-11-22T23:00:00Z', timeZone: 'Europe/Berlin',
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('table')?.textContent).toContain('gpt-6.2-nova');
    expect(fixture.nativeElement.querySelector('table')?.textContent).toContain('2,420,000');
    expect(fixture.nativeElement.textContent).toContain('Nov 16, 2026 to Nov 22, 2026 (Europe/Berlin)');
    expect(fixture.nativeElement.querySelector('table')?.textContent).toContain('Unpriced calls');
  });
});

import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioService } from '../../../../services/quality-studio.service';
import { QualityStudioConnectionSettingsComponent } from './quality-studio-connection-settings.component';

describe('QualityStudioConnectionSettingsComponent', () => {
  it('shows owner settings without exposing a stored service token', async () => {
    TestBed.configureTestingModule({
      imports: [QualityStudioConnectionSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
        { provide: QualityStudioService, useValue: { connectionChanged: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(QualityStudioConnectionSettingsComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/integrations/quality-studio/connection').flush({
      internalBaseUrl: 'http://quality-studio:8080', publicUrl: 'https://quality.test', serviceClientId: 'agent-studio',
    });
    await fixture.whenStable();
    const token = fixture.nativeElement.querySelector('[data-testid="quality-studio-token"]') as HTMLInputElement;
    expect(token.value).toBe('');
    expect(fixture.nativeElement.querySelector('[data-testid="quality-studio-public-url"]')?.value).toBe('https://quality.test');
    http.verify();
  });
});

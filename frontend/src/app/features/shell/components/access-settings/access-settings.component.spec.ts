import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { AuthService } from '../../../../services/auth.service';
import { AccessSettingsComponent } from './access-settings.component';

const owner = {
  profile: 'networked' as const, bootstrapRequired: false, authenticated: true,
  user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner' as const,
    projects: [], disabled: false, mustChangePassword: false },
};

function flushLists(http: HttpTestingController) {
  http.expectOne('/api/auth/users').flush([]);
  http.expectOne('/api/auth/api-tokens').flush([]);
  http.expectOne('/api/auth/runner-enrollments').flush([]);
  http.expectOne('/api/auth/runners').flush([]);
}

describe('AccessSettingsComponent', () => {
  it('loads all owner inventories and shows an API token only during creation', async () => {
    await TestBed.configureTestingModule({
      imports: [AccessSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const auth = TestBed.inject(AuthService);
    auth.status.set(owner);
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AccessSettingsComponent);
    fixture.detectChanges();
    flushLists(http);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('API tokens');
    expect(fixture.nativeElement.textContent).toContain('Runner enrollments');

    const component = fixture.componentInstance;
    component.tokenName.set('build bot');
    component.tokenProjects.set('PROJ-002');
    component.createToken();
    const secret = ['api', 'one', 'time-value'].join('.');
    const create = http.expectOne('/api/auth/api-tokens');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toMatchObject({ name: 'build bot', role: 'operator', projects: ['PROJ-002'] });
    create.flush({ token: { id: 'tok_1', name: 'build bot' }, secret });
    flushLists(http);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(secret);
    component.oneTime.set(null);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain(secret);

    component.runnerName.set('engine host');
    component.runnerKind.set('orchestrator');
    component.createEnrollment();
    const enrollment = http.expectOne('/api/auth/runner-enrollments');
    expect(enrollment.request.body).toMatchObject({ name: 'engine host', scopes: ['orchestration.write'] });
    enrollment.flush({ enrollmentCode: 'one-time-code' });
    flushLists(http);
    http.verify();
  });

  it('does not fetch owner inventories for a non-owner', async () => {
    await TestBed.configureTestingModule({
      imports: [AccessSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    TestBed.inject(AuthService).status.set({ ...owner, user: { ...owner.user, role: 'viewer' } });
    const fixture = TestBed.createComponent(AccessSettingsComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Sign in as the installation owner');
    TestBed.inject(HttpTestingController).expectNone('/api/auth/users');
  });

  it('shows an empty active list only after inventory loads and omits revoked tokens', async () => {
    await TestBed.configureTestingModule({
      imports: [AccessSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    TestBed.inject(AuthService).status.set(owner);
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AccessSettingsComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('No API tokens yet.');
    http.expectOne('/api/auth/users').flush([]);
    http.expectOne('/api/auth/api-tokens').flush([{ id: 'tok_1', name: 'retired bot', role: 'viewer',
      projects: ['PROJ-002'], createdBy: 'owner', createdAt: '2026-10-19T09:00:00Z',
      expiresAt: '2026-11-19T09:00:00Z', lastUsedAt: null, revokedAt: '2026-10-20T09:00:00Z' }]);
    http.expectOne('/api/auth/runner-enrollments').flush([]);
    http.expectOne('/api/auth/runners').flush([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No API tokens yet.');
    expect(fixture.nativeElement.textContent).not.toContain('retired bot');

    fixture.componentInstance.refresh();
    http.expectOne('/api/auth/users').flush([]);
    http.expectOne('/api/auth/api-tokens').flush({ message: 'Token service unavailable' },
      { status: 503, statusText: 'Service Unavailable' });
    http.expectOne('/api/auth/runner-enrollments');
    http.expectOne('/api/auth/runners');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Token service unavailable');
    expect(fixture.nativeElement.textContent).not.toContain('No API tokens yet.');
    http.verify({ ignoreCancelled: true });
  });
});

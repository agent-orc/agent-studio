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

  it.each([401, 403])('clears cached account data when a reload is denied with %i', async status => {
    await TestBed.configureTestingModule({
      imports: [AccessSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    TestBed.inject(AuthService).status.set(owner);
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AccessSettingsComponent);
    fixture.detectChanges();
    http.expectOne('/api/auth/users').flush([{
      id: 'usr_2', username: 'colleague', displayName: 'Colleague', role: 'operator',
      projects: [], disabled: false, mustChangePassword: false,
    }]);
    http.expectOne('/api/auth/api-tokens').flush([{
      id: 'tok_1', name: 'Cached token', role: 'operator', projects: [], createdBy: 'usr_owner',
      createdAt: '', expiresAt: '', lastUsedAt: null, revokedAt: null,
    }]);
    http.expectOne('/api/auth/runner-enrollments').flush([{
      id: 'enr_1', name: 'Cached enrollment', scopes: [], createdAt: '', expiresAt: '', usedAt: null,
    }]);
    http.expectOne('/api/auth/runners').flush([{
      id: 'run_1', name: 'Cached runner', createdAt: '', revokedAt: null, credentials: [],
    }]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Colleague');
    expect(fixture.nativeElement.textContent).toContain('Cached token');

    const component = fixture.componentInstance;
    component.edit(component.users()[0]);
    component.oneTime.set({ label: 'Temporary password', value: 'one-time-value' });
    component.refresh();
    http.expectOne('/api/auth/users').flush({ message: 'Denied Access' },
      { status, statusText: 'Denied Access' });
    http.match(() => true);
    fixture.detectChanges();

    expect(component.users()).toEqual([]);
    expect(component.tokens()).toEqual([]);
    expect(component.enrollments()).toEqual([]);
    expect(component.runners()).toEqual([]);
    expect(component.editId()).toBeNull();
    expect(component.oneTime()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Denied Access');
    expect(fixture.nativeElement.textContent).not.toContain('Colleague');
    expect(fixture.nativeElement.textContent).not.toContain('Cached token');
    expect(fixture.nativeElement.textContent).not.toContain('Cached enrollment');
    expect(fixture.nativeElement.textContent).not.toContain('Cached runner');
    expect(fixture.nativeElement.textContent).not.toContain('one-time-value');
    http.verify();
  });
});

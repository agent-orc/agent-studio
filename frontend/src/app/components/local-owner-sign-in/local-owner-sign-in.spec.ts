import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuthSessionState, type AuthStatus } from '../../services/auth.service';
import { LocalOwnerSignInComponent } from './local-owner-sign-in';

const owner = { id: 'usr_1', username: 'local.owner', displayName: 'Local Owner', role: 'owner' as const,
  projects: [], disabled: false, mustChangePassword: false };

describe('LocalOwnerSignInComponent', () => {
  let http: HttpTestingController;
  let state: AuthSessionState;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [LocalOwnerSignInComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    state = TestBed.inject(AuthSessionState);
  });
  afterEach(() => http.verify());

  async function render(status: AuthStatus) {
    state.status.set(status);
    const fixture = TestBed.createComponent(LocalOwnerSignInComponent);
    let signedIn = 0;
    fixture.componentInstance.signedIn.subscribe(() => signedIn++);
    fixture.detectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const type = (id: string, value: string) => {
      const input = element.querySelector<HTMLInputElement>(`[data-testid="${id}"]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    return { fixture, element, type, signedIn: () => signedIn };
  }

  it('creates the owner account in place on a fresh local installation', async () => {
    const { fixture, element, type, signedIn } = await render({ profile: 'local', bootstrapRequired: true, authenticated: false });
    const open = element.querySelector<HTMLButtonElement>('[data-testid="local-owner-open"]')!;
    expect(open.textContent).toContain('Create owner account');
    open.click();
    fixture.detectChanges();
    type('local-owner-username', 'local.owner');
    type('local-owner-display-name', 'Local Owner');
    type('local-owner-password', 'correct-horse-battery');
    element.querySelector<HTMLButtonElement>('[data-testid="local-owner-submit"]')!.click();
    const request = http.expectOne('/api/v1/studio/auth/bootstrap');
    expect(request.request.body).toEqual({ username: 'local.owner', password: 'correct-horse-battery', displayName: 'Local Owner' });
    request.flush({ profile: 'local', bootstrapRequired: false, authenticated: true, user: owner, secondFactorEnrollmentRequired: true });
    expect(signedIn()).toBe(1);
    expect(state.status()?.authenticated).toBe(true);
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="local-owner-signed-in"]')?.textContent).toContain('Local Owner');
  });

  it('signs in to the existing owner account and shows the server refusal', async () => {
    const { fixture, element, type, signedIn } = await render({ profile: 'local', bootstrapRequired: false, authenticated: false });
    fixture.componentRef.setInput('startOpen', true);
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="local-owner-display-name"]')).toBeNull();
    type('local-owner-username', 'local.owner');
    type('local-owner-password', 'wrong-password-123');
    element.querySelector<HTMLButtonElement>('[data-testid="local-owner-submit"]')!.click();
    http.expectOne('/api/v1/studio/auth/login').flush({ error: 'invalid-credentials', message: 'Invalid username or password.' },
      { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="local-owner-error"]')?.textContent).toContain('Invalid username or password.');
    expect(signedIn()).toBe(0);

    type('local-owner-password', 'correct-horse-battery');
    element.querySelector<HTMLButtonElement>('[data-testid="local-owner-submit"]')!.click();
    const retry = http.expectOne('/api/v1/studio/auth/login');
    expect(retry.request.body.username).toBe('local.owner');
    retry.flush({ profile: 'local', bootstrapRequired: false, authenticated: true, user: owner });
    expect(signedIn()).toBe(1);
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessInviteFormComponent } from './access-invite-form.component';

describe('AccessInviteFormComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessInviteFormComponent],
      providers: [provideZonelessChangeDetection(), { provide: AccessService, useValue: access }],
    }).compileComponents();
  });

  function type(element: HTMLElement, testId: string, value: string): void {
    const input = element.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement | HTMLSelectElement;
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
  }

  it('invites with one role binding and shows the one-time sign-in link', async () => {
    const fixture = TestBed.createComponent(AccessInviteFormComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const submit = element.querySelector('[data-testid="access-invite-submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    type(element, 'access-invite-username', 'new.user');
    type(element, 'access-invite-display-name', 'New User');
    type(element, 'access-grant-project', 'PROJ-001');
    type(element, 'access-grant-role', 'contributor');
    fixture.detectChanges();
    submit.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(access.invite).toHaveBeenCalledWith('new.user', 'New User', [{ role: 'contributor', scope: { kind: 'project', project: 'PROJ-001' } }]);
    const link = element.querySelector('[data-testid="access-invite-link"]')?.textContent ?? '';
    expect(link).toContain(`${location.origin}/?invite=inv.abc`);
    expect(link).toContain('21 Oct 2026, 09:00 UTC');
    expect(link).toContain('only to new.user');
    expect(link).toContain('works once');
    expect(link).toContain('second factor is recommended');
    expect(link).toContain('Shown once');
    expect(element.querySelector('[data-testid="access-invite-link-value"]')?.textContent).toBe(`${location.origin}/?invite=inv.abc`);
    expect(element.querySelector('[data-testid="access-invite-link-copy"]')?.textContent).toContain('Copy link');
  });

  it('hides the first link and names the next invite while its request is pending', async () => {
    const fixture = TestBed.createComponent(AccessInviteFormComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    type(element, 'access-invite-username', 'first.user');
    type(element, 'access-grant-project', 'PROJ-001');
    type(element, 'access-grant-role', 'contributor');
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-invite-submit"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-invite-link"]')?.textContent).toContain('/?invite=inv.abc');

    let finishSecondInvite!: (value: Awaited<ReturnType<typeof access.invite>>) => void;
    const secondInvite = new Promise<Awaited<ReturnType<typeof access.invite>>>(resolve => { finishSecondInvite = resolve; });
    access.invite.mockReturnValueOnce(secondInvite);
    type(element, 'access-invite-username', 'reviewer.user');
    type(element, 'access-grant-role', 'viewer');
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-invite-submit"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(access.invite).toHaveBeenLastCalledWith('reviewer.user', '', [{ role: 'viewer', scope: { kind: 'project', project: 'PROJ-001' } }]);
    expect(element.querySelector('[data-testid="access-invite-link"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-invite-link-copy"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-invite-pending"]')?.textContent).toContain('reviewer.user');
    expect(element.querySelector('[data-testid="access-invite-pending"]')?.textContent).toContain('Viewer');
    expect((element.querySelector('[data-testid="access-invite-submit"]') as HTMLButtonElement).disabled).toBe(true);

    finishSecondInvite({ user: { username: 'reviewer.user' }, signInLink: '/?invite=inv.second', expiresAt: '2026-10-21T09:00:00Z' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-invite-pending"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-invite-link"]')?.textContent).toContain('/?invite=inv.second');
  });
});

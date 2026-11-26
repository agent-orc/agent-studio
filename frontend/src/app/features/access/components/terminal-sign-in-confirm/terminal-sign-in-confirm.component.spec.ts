import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TerminalSignInConfirmation, TerminalSignInLookup } from '../../models/terminal-sign-in.model';
import { TerminalSignInRouteService } from '../../services/terminal-sign-in-route.service';
import { TerminalSignInService, type TerminalSignInChoice } from '../../services/terminal-sign-in.service';
import type { GrantStepUp } from '../../services/standing-grants.service';
import { TerminalSignInConfirmComponent } from './terminal-sign-in-confirm.component';

function lookup(overrides: Partial<TerminalSignInLookup> = {}): TerminalSignInLookup {
  return {
    signInId: 'sign-in-1', userCode: 'WDJB-MJHT', state: 'pending', deviceLabel: 'opsagent@workstation',
    keyThumbprint: 'NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs', knownForAgentIds: [], requestedAgent: 'ops console',
    requestedAgentId: 'opa_ops', requestedClasses: [
      { id: 'cards.accept-override', title: 'Accept with override', mode: 'batch' },
      { id: 'project.testing', title: 'Set the testing strategy', mode: 'direct' },
    ],
    requestedAddress: '198.51.100.7', browserAddress: '203.0.113.20', addressMatches: false,
    createdAt: '2026-11-23T09:00:00Z', expiresAt: '2026-11-23T09:10:00Z', lifetimeHours: [1, 2, 3, 4, 5, 6, 7, 8],
    defaultLifetimeHours: 8, agents: [{ id: 'opa_ops', name: 'ops console' }, { id: 'opa_night', name: 'night shift' }],
    ...overrides,
  };
}

function fakeSignIn(found: TerminalSignInLookup | null = lookup()) {
  const service = {
    lookup: signal<TerminalSignInLookup | null>(null),
    confirmed: signal<TerminalSignInConfirmation | null>(null),
    denied: signal(false),
    busy: signal(false),
    error: signal<string | null>(null),
    errorCode: signal<string | null>(null),
    reset: vi.fn(() => { service.lookup.set(null); service.confirmed.set(null); service.denied.set(false); service.error.set(null); }),
    find: vi.fn(async () => {
      if (!found) { service.error.set('No terminal sign-in has this code.'); service.errorCode.set('signin-code-unknown'); return null; }
      service.lookup.set(found);
      return found;
    }),
    confirm: vi.fn(async (choice: TerminalSignInChoice, ...rest: [GrantStepUp]) => {
      void rest;
      const done = { signInId: 'sign-in-1', operatorAgentId: choice.operatorAgentId, sessionId: 'ses_1',
        actor: `operator-agent:${choice.operatorAgentId}/session:ses_1`, expiresAt: '2026-11-23T17:00:00Z', classes: choice.classes };
      service.confirmed.set(done);
      return done;
    }),
    deny: vi.fn(async (...code: [string]) => { void code; service.denied.set(true); return true; }),
  };
  return service;
}

describe('TerminalSignInConfirmComponent', () => {
  let service: ReturnType<typeof fakeSignIn>;
  let route: { request: ReturnType<typeof signal>; close: ReturnType<typeof vi.fn> };
  let fixture: ComponentFixture<TerminalSignInConfirmComponent>;
  let element: HTMLElement;

  async function render(linkCode: string | null, found: TerminalSignInLookup | null = lookup()): Promise<void> {
    service = fakeSignIn(found);
    route = { request: signal({ code: linkCode }), close: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [TerminalSignInConfirmComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TerminalSignInService, useValue: service },
        { provide: TerminalSignInRouteService, useValue: route },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(TerminalSignInConfirmComponent);
    fixture.componentRef.setInput('linkCode', linkCode);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => TestBed.resetTestingModule());

  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => element.querySelector(`[data-testid="${id}"]`) as T | null;
  const click = async (id: string) => {
    byTestId(id)!.click();
    await fixture.whenStable();
    fixture.detectChanges();
  };

  it('shows the link code, the device, the unknown key, the address mismatch and requires the tick', async () => {
    await render('WDJBMJHT');
    expect(service.find).toHaveBeenCalledWith('WDJBMJHT');
    expect(byTestId('terminal-sign-in-code')?.textContent).toBe('WDJB-MJHT');
    expect(byTestId('terminal-sign-in-device')?.textContent).toBe('opsagent@workstation');
    expect(byTestId('terminal-sign-in-known')?.textContent).toContain('Unknown device');
    expect(byTestId('terminal-sign-in-address')?.textContent).toBe('198.51.100.7');
    expect(byTestId('terminal-sign-in-address-match')?.textContent).toContain('Other network than this browser');
    expect(byTestId('terminal-sign-in-warning')).not.toBeNull();
    expect(byTestId('terminal-sign-in-thumbprint')?.getAttribute('title')).toBe(lookup().keyThumbprint);
    expect(byTestId<HTMLSelectElement>('terminal-sign-in-agent')!.value).toBe('opa_ops');
    expect(byTestId<HTMLSelectElement>('terminal-sign-in-lifetime')!.value).toBe('8');
    expect(byTestId<HTMLButtonElement>('terminal-sign-in-confirm')!.disabled).toBe(true);

    const tick = byTestId<HTMLInputElement>('terminal-sign-in-code-match')!;
    tick.checked = true;
    tick.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(byTestId<HTMLButtonElement>('terminal-sign-in-confirm')!.disabled).toBe(false);
  });

  it('confirms with the kept classes, the lifetime and the entered code, never adding a class', async () => {
    await render('WDJBMJHT');
    const tick = byTestId<HTMLInputElement>('terminal-sign-in-code-match')!;
    tick.checked = true;
    tick.dispatchEvent(new Event('change'));
    const testing = byTestId<HTMLInputElement>('terminal-sign-in-class-project.testing')!;
    testing.checked = false;
    testing.dispatchEvent(new Event('change'));
    const lifetime = byTestId<HTMLSelectElement>('terminal-sign-in-lifetime')!;
    lifetime.value = '2';
    lifetime.dispatchEvent(new Event('change'));
    const code = String(100_000 + Math.floor(Math.random() * 899_999));
    const factor = byTestId<HTMLInputElement>('terminal-sign-in-factor')!;
    factor.value = code;
    factor.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    await click('terminal-sign-in-confirm');
    expect(service.confirm).toHaveBeenCalledWith(
      { code: 'WDJBMJHT', operatorAgentId: 'opa_ops', classes: ['cards.accept-override'], lifetimeHours: 2 }, { code });
    expect(byTestId('terminal-sign-in-confirmed')?.textContent).toContain('operator-agent:opa_ops/session:ses_1');
  });

  it('uses a passkey when no code is entered and shows a known device on the same network quietly', async () => {
    await render(null, lookup({ knownForAgentIds: ['opa_ops'], addressMatches: true, requestedClasses: [] }));
    expect(byTestId('terminal-sign-in-facts')).toBeNull();
    const input = byTestId<HTMLInputElement>('terminal-sign-in-code-input')!;
    input.value = 'wdjb-mjht';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await click('terminal-sign-in-lookup');
    expect(service.find).toHaveBeenCalledWith('WDJBMJHT');
    expect(byTestId('terminal-sign-in-known')?.textContent).toContain('Known device');
    expect(byTestId('terminal-sign-in-warning')).toBeNull();
    expect(byTestId('terminal-sign-in-classes')?.textContent).toContain('requested no action classes');
    expect(byTestId<HTMLButtonElement>('terminal-sign-in-confirm')!.disabled).toBe(false);

    await click('terminal-sign-in-confirm');
    expect(service.confirm.mock.calls[0][1]).toEqual({ passkey: true });
  });

  it('shows the refusal of an unknown code and offers code entry after a link', async () => {
    await render('WDJBMJHT', null);
    expect(byTestId('terminal-sign-in-error')?.textContent).toContain('No terminal sign-in has this code.');
    expect(byTestId('terminal-sign-in-error')?.getAttribute('data-code')).toBe('signin-code-unknown');
    await click('terminal-sign-in-enter-code');
    expect(byTestId('terminal-sign-in-code-input')).not.toBeNull();
  });

  it('denies without a second factor and closes back to the board', async () => {
    await render('WDJBMJHT');
    await click('terminal-sign-in-deny');
    expect(service.deny).toHaveBeenCalledWith('WDJBMJHT');
    expect(byTestId('terminal-sign-in-denied')).not.toBeNull();
    await click('terminal-sign-in-done');
    expect(route.close).toHaveBeenCalled();
  });
});

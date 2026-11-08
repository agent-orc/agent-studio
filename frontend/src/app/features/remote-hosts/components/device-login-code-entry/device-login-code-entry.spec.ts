import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { DeviceLoginCodeEntryComponent } from './device-login-code-entry';

describe('DeviceLoginCodeEntryComponent', () => {
  const providerAuth = { submitDeviceLoginCode: vi.fn() };

  beforeEach(() => {
    providerAuth.submitDeviceLoginCode.mockReset();
    TestBed.configureTestingModule({ providers: [
      provideZonelessChangeDetection(),
      { provide: ProviderAuthStatusService, useValue: providerAuth },
    ] });
  });

  function render(codeState: 'awaiting' | 'submitted' | 'delivered' | null) {
    const fixture = TestBed.createComponent(DeviceLoginCodeEntryComponent);
    fixture.componentRef.setInput('handle', 'dlogin_a');
    fixture.componentRef.setInput('sessionKey', 'key-a');
    fixture.componentRef.setInput('providerLabel', 'Claude');
    fixture.componentRef.setInput('codeState', codeState);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const input = () => host.querySelector<HTMLInputElement>('[data-testid="device-login-auth-code"]');
    const button = () => host.querySelector<HTMLButtonElement>('[data-testid="device-login-auth-code-submit"]');
    const type = (value: string) => {
      input()!.value = value;
      input()!.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    return { fixture, host, input, button, type };
  }

  it('renders nothing for a session that takes no code back', () => {
    const { host } = render(null);
    expect(host.textContent?.trim()).toBe('');
  });

  it('sends the trimmed code once with the session key and drops the typed value', () => {
    providerAuth.submitDeviceLoginCode.mockReturnValue(of(undefined));
    const { fixture, host, input, button, type } = render('awaiting');
    expect(button()!.disabled).toBe(true);
    type('  pasted-code#state  ');
    expect(button()!.disabled).toBe(false);
    button()!.click();
    fixture.detectChanges();
    expect(providerAuth.submitDeviceLoginCode).toHaveBeenCalledTimes(1);
    expect(providerAuth.submitDeviceLoginCode).toHaveBeenCalledWith('dlogin_a', 'key-a', 'pasted-code#state');
    expect(fixture.componentInstance.code()).toBe('');
    expect(input()).toBeNull();
    expect(host.querySelector('[data-testid="device-login-code-sent"]')?.textContent)
      .toContain('Code sent to the host. Waiting for Claude to confirm the sign-in.');
  });

  it('keeps the field and shows the server reason when the code is refused', () => {
    providerAuth.submitDeviceLoginCode.mockReturnValue(throwError(() => ({
      error: { message: 'Paste the complete authorization code shown after the Claude sign-in.' } })));
    const { fixture, host, input, button, type } = render('awaiting');
    type('not a code');
    button()!.click();
    fixture.detectChanges();
    expect(host.querySelector('[role="alert"]')?.textContent)
      .toContain('Paste the complete authorization code shown after the Claude sign-in.');
    expect(input()).not.toBeNull();
    expect(button()!.disabled).toBe(false);
  });

  it('shows the sent state that the server reports', () => {
    for (const state of ['submitted', 'delivered'] as const) {
      const { host, input } = render(state);
      expect(input()).toBeNull();
      expect(host.querySelector('[data-testid="device-login-code-sent"]')).not.toBeNull();
    }
  });
});

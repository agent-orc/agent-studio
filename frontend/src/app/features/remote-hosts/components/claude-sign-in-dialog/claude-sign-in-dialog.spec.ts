import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NotificationService } from '../../../../services/notification.service';
import type {
  ClaudeSignInStartResponse,
  ClaudeSignInStatusResponse,
  ClaudeSignInTarget,
  ProviderAuthBadge,
} from '../../models/provider-auth.model';
import { ClaudeSignInDialogService } from '../../services/claude-sign-in-dialog.service';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { ClaudeSignInDialogComponent } from './claude-sign-in-dialog';

const TARGET: ClaudeSignInTarget = {
  hostId: 'host-01',
  runnerId: 'runner-01',
  hostName: 'Linux runner',
  aliases: ['host-01', 'runner-01', 'Linux runner'],
  sshTarget: 'agent@runner-01',
  baselineAdvertisedAt: '2026-09-06T10:00:00Z',
};

const STARTED: ClaudeSignInStartResponse = {
  handle: 'claude_session_1',
  state: 'pending',
  verificationUrl: 'https://claude.ai/setup-token/abc123',
  expiresAt: '2026-09-06T10:15:00Z',
};

class FakeDialogStore {
  readonly request = signal<ClaudeSignInTarget | null>(null);
  close = vi.fn(() => this.request.set(null));
  refreshHosts = vi.fn();
}

class FakeProviderAuth {
  readonly statuses = signal<readonly ProviderAuthBadge[]>([]);
  readonly status = new Subject<ClaudeSignInStatusResponse>();
  probeResult: Observable<ProviderAuthBadge> = of(readyBadge());

  startClaudeSignIn = vi.fn(() => of(STARTED));
  claudeSignInStatus = vi.fn(() => this.status.asObservable());
  waitForFreshProbe = vi.fn(() => this.probeResult);
}

describe('ClaudeSignInDialogComponent', () => {
  let store: FakeDialogStore;
  let providerAuth: FakeProviderAuth;
  let fixture: ReturnType<typeof TestBed.createComponent<ClaudeSignInDialogComponent>>;

  beforeEach(() => {
    store = new FakeDialogStore();
    providerAuth = new FakeProviderAuth();
    TestBed.configureTestingModule({
      imports: [ClaudeSignInDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ClaudeSignInDialogService, useValue: store },
        { provide: ProviderAuthStatusService, useValue: providerAuth },
        { provide: NotificationService, useValue: { success: vi.fn() } },
      ],
    });
    fixture = TestBed.createComponent(ClaudeSignInDialogComponent);
  });

  afterEach(() => {
    vi.useRealTimers();
    fixture.destroy();
    document.querySelectorAll('[data-testid="claude-sign-in-dialog-overlay"]').forEach(node => node.remove());
  });

  it('shows the secure sign-in link while pending', async () => {
    store.request.set(TARGET);
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = document.querySelector('[data-testid="claude-sign-in-dialog"]') as HTMLElement;
    expect(providerAuth.startClaudeSignIn).toHaveBeenCalledWith('host-01', 'agent@runner-01');
    expect(dialog.querySelector('[data-testid="claude-sign-in-pending"]')).toBeTruthy();
    expect(dialog.querySelector<HTMLAnchorElement>('[data-testid="claude-sign-in-url"]')?.href)
      .toBe('https://claude.ai/setup-token/abc123');
    expect(dialog.textContent).toContain('Studio does not receive the resulting token');
  });

  it('renders the failed state reported by the remote process', async () => {
    vi.useFakeTimers();
    store.request.set(TARGET);
    await fixture.whenStable();
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(500);
    providerAuth.status.next({
      handle: STARTED.handle,
      state: 'failed',
      detail: 'Claude sign-in did not complete or login status could not be verified.',
      requestedAt: '2026-09-06T10:00:00Z',
      expiresAt: STARTED.expiresAt,
      completedAt: '2026-09-06T10:01:00Z',
    });
    fixture.detectChanges();

    expect(document.querySelector('[data-testid="claude-sign-in-failed"]')?.textContent)
      .toContain('login status could not be verified');
    expect(document.querySelector('[data-testid="claude-sign-in-retry"]')).toBeTruthy();
    vi.useRealTimers();
  });

  it('waits for a fresh OK provider probe and then closes', async () => {
    vi.useFakeTimers();
    store.request.set(TARGET);
    await fixture.whenStable();
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(500);
    providerAuth.status.next({
      handle: STARTED.handle,
      state: 'completed',
      detail: 'Claude sign-in completed.',
      requestedAt: '2026-09-06T10:00:00Z',
      expiresAt: STARTED.expiresAt,
      completedAt: '2026-09-06T10:01:00Z',
    });
    fixture.detectChanges();
    await fixture.whenStable();

    expect(providerAuth.waitForFreshProbe).toHaveBeenCalledWith(
      'claude', TARGET.aliases, TARGET.baselineAdvertisedAt);
    expect(store.refreshHosts).toHaveBeenCalledOnce();
    expect(store.close).toHaveBeenCalledOnce();
    expect(store.request()).toBeNull();
    vi.useRealTimers();
  });
});

function readyBadge(): ProviderAuthBadge {
  return {
    id: 'runner-01:claude', provider: 'claude', providerLabel: 'Claude',
    runnerId: 'runner-01', hostId: 'host-01', hostName: 'Linux runner', aliases: TARGET.aliases,
    state: 'ok', signal: 'ok', detail: 'Active session confirmed.',
    advertisedAt: '2026-09-06T10:01:00Z', lastSeenAt: '2026-09-06T10:01:00Z',
    consecutiveFailures: 0, reachable: true,
    expiresAt: null, expiresSoon: false, expiryLabel: null, limitedUntil: null, history: [],
  };
}

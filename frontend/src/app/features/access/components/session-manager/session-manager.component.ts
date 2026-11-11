import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService, type AuthSessionSummary } from '../../../../services/auth.service';
import { createPasskey, getPasskey } from '../../../../services/webauthn';

@Component({
  selector: 'app-session-manager',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './session-manager.component.html',
  styleUrl: './session-manager.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SessionManagerComponent implements OnInit, OnDestroy {
  private readonly auth = inject(AuthService);
  readonly userId = input<string | null>(null);
  readonly recoveryMode = input(false);
  readonly watchNotices = input(false);
  readonly open = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly sessions = signal<AuthSessionSummary[]>([]);
  readonly totpSecret = signal('');
  readonly totpCode = signal('');
  readonly recoveryCodes = signal<string[]>([]);
  readonly factorNotice = signal('');
  readonly notice = signal(false);
  readonly stepUpCode = signal('');
  readonly enrollmentStepUpCode = signal('');
  readonly pendingEnrollment = signal<'totp' | 'passkey' | null>(null);
  readonly enrollmentAuthorized = signal(false);
  readonly pendingRevocation = signal<{ id?: string } | null>(null);
  private readonly noticed = new Set<string>();
  private poll: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    if (!this.watchNotices()) return;
    void this.load();
    this.poll = setInterval(() => { void this.load(); }, 30_000);
  }

  ngOnDestroy(): void {
    if (this.poll) clearInterval(this.poll);
  }

  dismissNotice(): void { this.notice.set(false); }

  async toggle(): Promise<void> {
    this.open.update(value => !value);
    if (this.open()) await this.load();
  }

  async load(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await firstValueFrom(this.auth.listSessions(this.userId()));
      const sessions = Array.isArray(response) ? response : [];
      this.sessions.set(sessions);
      if (this.watchNotices()) {
        const current = sessions.find(session => session.current);
        for (const session of sessions) {
          const id = this.sessionId(session);
          if (!session.current && current && session.createdAt > current.createdAt && !this.noticed.has(id)) {
            this.notice.set(true);
            this.noticed.add(id);
          }
        }
      }
    }
    catch { this.error.set('Sessions could not be loaded.'); }
    finally { this.busy.set(false); }
  }

  async revoke(id: string): Promise<void> {
    if (this.userId()) { this.pendingRevocation.set({ id }); return; }
    await this.performRevocation(id);
  }

  private async performRevocation(id?: string): Promise<void> {
    this.busy.set(true);
    try {
      if (id) await firstValueFrom(this.auth.revokeSession(id, this.userId()));
      else await firstValueFrom(this.auth.revokeOtherSessions(this.userId()));
      this.pendingRevocation.set(null);
      this.stepUpCode.set('');
      await this.load();
    } catch { this.error.set('Sessions could not be revoked. Verify again and retry.'); }
    finally { this.busy.set(false); }
  }

  async revokeOthers(): Promise<void> {
    if (this.userId()) { this.pendingRevocation.set({}); return; }
    await this.performRevocation();
  }

  async confirmStepUp(usePasskey = false): Promise<void> {
    const action = this.pendingRevocation();
    if (!action) return;
    this.busy.set(true);
    this.error.set('');
    try {
      if (usePasskey) {
        const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
        await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
      } else await firstValueFrom(this.auth.stepUp(this.stepUpCode()));
      await this.performRevocation(action.id);
    } catch { this.error.set('Second factor could not be verified.'); }
    finally { this.busy.set(false); }
  }

  sessionId(session: AuthSessionSummary): string { return session.id ?? session.sessionId ?? ''; }

  async requestEnrollment(kind: 'totp' | 'passkey'): Promise<void> {
    if (this.auth.status()?.user?.hasFactor && !this.auth.status()?.recoveryEnrollmentAllowed
        && !this.enrollmentAuthorized()) {
      this.pendingEnrollment.set(kind);
      return;
    }
    if (kind === 'totp') await this.beginTotp();
    else await this.addPasskey();
  }

  async confirmEnrollmentStepUp(usePasskey = false): Promise<void> {
    const kind = this.pendingEnrollment();
    if (!kind) return;
    this.busy.set(true);
    this.error.set('');
    try {
      if (usePasskey) {
        const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
        await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
      } else await firstValueFrom(this.auth.stepUp(this.enrollmentStepUpCode()));
      this.enrollmentAuthorized.set(true);
      this.pendingEnrollment.set(null);
      this.enrollmentStepUpCode.set('');
    } catch { this.error.set('Current factor could not be verified.'); }
    finally { this.busy.set(false); }
    if (this.enrollmentAuthorized()) {
      if (kind === 'totp') await this.beginTotp();
      else await this.addPasskey();
    }
  }

  async beginTotp(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try { this.totpSecret.set((await firstValueFrom(this.auth.beginTotp())).secret); }
    catch (error) {
      if (this.stepUpExpired(error)) { this.pendingEnrollment.set('totp'); this.enrollmentAuthorized.set(false); }
      this.error.set(this.stepUpExpired(error) ? 'Verify your current factor again.' : 'Authenticator setup could not start.');
    }
    finally { this.busy.set(false); }
  }

  async confirmTotp(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      this.recoveryCodes.set((await firstValueFrom(this.auth.confirmTotp(this.totpCode()))).recoveryCodes);
      this.totpSecret.set('');
      this.totpCode.set('');
      this.factorNotice.set('New authenticator registered. Store the new recovery codes.');
      this.enrollmentAuthorized.set(false);
      this.auth.initialize();
    } catch (error) {
      if (this.stepUpExpired(error)) {
        this.pendingEnrollment.set('totp');
        this.enrollmentAuthorized.set(false);
      }
      this.error.set(this.stepUpExpired(error) ? 'Verify your current factor again.' : 'The authenticator code is invalid.');
    }
    finally { this.busy.set(false); }
  }

  async addPasskey(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      const options = await firstValueFrom(this.auth.passkeyRegistrationOptions());
      const proof = await createPasskey(options);
      this.recoveryCodes.set((await firstValueFrom(this.auth.registerPasskey(proof))).recoveryCodes);
      this.factorNotice.set('New passkey registered.');
      this.enrollmentAuthorized.set(false);
      this.auth.initialize();
    } catch (error) {
      if (this.stepUpExpired(error)) { this.pendingEnrollment.set('passkey'); this.enrollmentAuthorized.set(false); }
      this.error.set(this.stepUpExpired(error) ? 'Verify your current factor again.'
        : error instanceof Error ? error.message : 'Passkey setup failed.');
    }
    finally { this.busy.set(false); }
  }

  private stepUpExpired(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 403 && error.error?.error === 'step-up-required';
  }
}

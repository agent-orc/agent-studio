import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService, type AuthSessionSummary } from '../../../../services/auth.service';
import { createPasskey } from '../../../../services/webauthn';

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
  readonly watchNotices = input(false);
  readonly open = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly sessions = signal<AuthSessionSummary[]>([]);
  readonly totpSecret = signal('');
  readonly totpCode = signal('');
  readonly recoveryCodes = signal<string[]>([]);
  readonly notice = signal(false);
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
    this.busy.set(true);
    try {
      await firstValueFrom(this.auth.revokeSession(id, this.userId()));
      await this.load();
    } catch { this.error.set('Session could not be revoked.'); }
    finally { this.busy.set(false); }
  }

  async revokeOthers(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.auth.revokeOtherSessions(this.userId()));
      await this.load();
    } catch { this.error.set('Other sessions could not be revoked.'); }
    finally { this.busy.set(false); }
  }

  sessionId(session: AuthSessionSummary): string { return session.id ?? session.sessionId ?? ''; }

  async beginTotp(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try { this.totpSecret.set((await firstValueFrom(this.auth.beginTotp())).secret); }
    catch { this.error.set('Authenticator setup could not start.'); }
    finally { this.busy.set(false); }
  }

  async confirmTotp(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      this.recoveryCodes.set((await firstValueFrom(this.auth.confirmTotp(this.totpCode()))).recoveryCodes);
      this.totpSecret.set('');
      this.totpCode.set('');
    } catch { this.error.set('The authenticator code is invalid.'); }
    finally { this.busy.set(false); }
  }

  async addPasskey(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      const options = await firstValueFrom(this.auth.passkeyRegistrationOptions());
      const proof = await createPasskey(options);
      this.recoveryCodes.set((await firstValueFrom(this.auth.registerPasskey(proof))).recoveryCodes);
    } catch (error) { this.error.set(error instanceof Error ? error.message : 'Passkey setup failed.'); }
    finally { this.busy.set(false); }
  }
}

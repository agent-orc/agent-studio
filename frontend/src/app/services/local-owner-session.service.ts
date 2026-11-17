import { Injectable, computed, inject, signal } from '@angular/core';
import { AuthSessionState } from './auth.service';

/** An action that waits until the person created or signed in to the owner account. */
export interface LocalOwnerRequest {
  /** One sentence naming why the account is needed, shown above the form. */
  readonly purpose: string;
  readonly resume: () => void;
}

/**
 * The local profile opens Studio without a session, yet production approval,
 * runner enrollment, and Access still need a signed-in Owner on the server
 * (DeployApprovalAuthority, RequireOwner). This service lets such an action
 * ask for that account in place and then finish. Other profiles always have
 * a session behind the shell, so their actions run directly.
 */
@Injectable({ providedIn: 'root' })
export class LocalOwnerSessionService {
  private readonly auth = inject(AuthSessionState);
  readonly request = signal<LocalOwnerRequest | null>(null);
  /** The local profile and no signed-in person. */
  readonly missing = computed(() => {
    const status = this.auth.status();
    return status?.profile === 'local' && status.authenticated !== true;
  });

  /** Runs the action now, or once the owner account is created or signed in. */
  run(purpose: string, action: () => void): void {
    if (!this.missing()) { action(); return; }
    this.request.set({ purpose, resume: action });
  }

  complete(): void {
    const pending = this.request();
    this.request.set(null);
    pending?.resume();
  }

  cancel(): void { this.request.set(null); }
}
